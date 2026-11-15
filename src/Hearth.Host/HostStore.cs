using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hearth.Core;

namespace Hearth.Host;

public sealed class HostException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
public sealed record HostSettings(string HostId, string Name, string OwnerTokenHash, int Port = 17842, string Bind = "127.0.0.1");
public sealed record StoredMember(string Id, string DisplayName, string Status, string Role, string TokenHash, DateTimeOffset RequestedAt, string? InviteLabel = null, string? InviteId = null)
{
    public MemberInfo Info() => new(Id, DisplayName, Status, Role, TokenHash[..12], RequestedAt, InviteLabel, InviteId);
}
public sealed record PackState(PackSummary Pack, List<StoredMember> Members, List<PersonalInviteInfo>? PersonalInvites = null);
public sealed record HostStatus(DateTimeOffset StartedAt, DateTimeOffset? LastPlayerAccessAt, string? LastPlayerName, string? LastPackName, string? LastOperation,
    long SuccessfulPlayerRequests, DateTimeOffset? LastInternetPlayerAccessAt = null, string? LastInternetPlayerName = null);

public sealed partial class HostStore : IDisposable
{
    public const long MaxBlobBytes = 512L * 1024 * 1024;
    private readonly string data;
    private readonly FileStream processLock;
    private readonly object gate = new();
    private HostStatus status = new(DateTimeOffset.UtcNow, null, null, null, null, 0);
    public HostSettings Settings { get; }

    public static void Initialize(HostArguments args)
    {
        if (args.OwnerTokenFile is null) throw new ArgumentException("Initialize using --owner-token-file with a file containing a random owner credential of at least 32 characters.");
        // App-owned setup keeps a delete-on-close handle so a launcher crash
        // cannot strand a plaintext credential file. Allow that handle to coexist.
        using var tokenStream = new FileStream(args.OwnerTokenFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var tokenReader = new StreamReader(tokenStream);
        var token = tokenReader.ReadToEnd().Trim();
        if (token.Length < 32 || token.Length > 512) throw new ArgumentException("The owner credential must have 32–512 characters.");
        Directory.CreateDirectory(args.Data);
        var path = Path.Combine(args.Data, "host.json");
        if (File.Exists(path)) throw new InvalidOperationException("This host is already initialized. Its owner credential was not changed.");
        WriteJson(path, new HostSettings(Guid.NewGuid().ToString("N"), "Hearth Host", HashToken(token), args.Port ?? 17842, args.Bind ?? "127.0.0.1"), overwrite: false);
    }

    public HostStore(HostArguments args)
    {
        data = args.Data;
        var path = Path.Combine(data, "host.json");
        if (!File.Exists(path)) throw new InvalidOperationException("Hearth host is not initialized. Enable hosting in Hearth first, or run --init --owner-token-file <file>.");
        Settings = ReadJson<HostSettings>(path);
        Directory.CreateDirectory(Path.Combine(data, "packs"));
        processLock = new FileStream(Path.Combine(data, ".host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        foreach (var packDirectory in Directory.EnumerateDirectories(Path.Combine(data, "packs")))
        {
            var staging = Path.Combine(packDirectory, "staging");
            if (Directory.Exists(staging))
                foreach (var file in Directory.EnumerateFiles(staging, "*.part")) File.Delete(file);
        }
    }

    public List<PackSummary> ListOwned(string token)
    {
        lock (gate)
        {
            var hash = HashToken(token);
            var isAdmin = IsAdmin(hash);
            var packs = Directory.EnumerateDirectories(Path.Combine(data, "packs"))
                .Where(x => File.Exists(Path.Combine(x, "state.json")))
                .Select(x => ReadJson<PackState>(Path.Combine(x, "state.json")))
                .Where(x => isAdmin || x.Members.Any(m => EqualsHash(m.TokenHash, hash) && m.Status == "approved" && m.Role == "owner"))
                .Select(Summary).OrderBy(x => x.Name).ToList();
            if (!isAdmin && packs.Count == 0) throw new HostException(403, "This credential cannot manage this host.");
            return packs;
        }
    }

    private static PackSummary Summary(PackState state) => state.Pack with { PendingCount = state.Members.Count(m => m.Status == "pending") };

    public HostEndpoints Endpoints(string token)
    {
        if (!IsAdmin(HashToken(token))) throw new HostException(403, "Only the host administrator can view host connection addresses.");
        var empty = new HostEndpoints(Settings.HostId, null, null, [], DateTimeOffset.UtcNow);
        var path = Path.Combine(data, "advertised-endpoints.json");
        if (!File.Exists(path)) return empty;
        try
        {
            if (new FileInfo(path).Length > 65536) throw new InvalidDataException();
            var endpoints = ReadJson<HostEndpoints>(path);
            if (endpoints.HostId != Settings.HostId || endpoints.HomeNetworkAddresses is null || endpoints.HomeNetworkAddresses.Count > 32) throw new InvalidDataException();
            var addresses = endpoints.HomeNetworkAddresses.Concat(endpoints.InternetAddress is null ? [] : new[] { endpoints.InternetAddress }).ToArray();
            if (addresses.Length == 0) return empty;
            if (endpoints.CertificatePin is null) throw new InvalidDataException();
            var pin = HostCertificate.NormalizePin(endpoints.CertificatePin);
            foreach (var address in addresses)
                if (!HearthApi.NormalizeHost(address).StartsWith("https://", StringComparison.Ordinal)) throw new InvalidDataException();
            return endpoints with { CertificatePin = pin };
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or InvalidDataException)
        { return empty; } // A partial/stale advertisement must never produce an untrusted link.
    }

    public HostStatus Status(string token)
    {
        lock (gate)
        {
            if (!IsAdmin(HashToken(token))) throw new HostException(403, "Only the host owner can view host status.");
            return status;
        }
    }

    public void NotePlayerAccess(string id, string token, string operation, bool internet = false)
    {
        lock (gate)
        {
            var state = Get(id);
            var hash = HashToken(token);
            var member = state.Members.SingleOrDefault(x => EqualsHash(x.TokenHash, hash) && x.Status == "approved");
            if (member is not null)
            {
                var time = DateTimeOffset.UtcNow;
                status = status with { LastPlayerAccessAt = time, LastPlayerName = member.DisplayName, LastPackName = state.Pack.Name,
                    LastOperation = operation, SuccessfulPlayerRequests = status.SuccessfulPlayerRequests + 1,
                    LastInternetPlayerAccessAt = internet ? time : status.LastInternetPlayerAccessAt,
                    LastInternetPlayerName = internet ? member.DisplayName : status.LastInternetPlayerName };
            }
        }
    }

    public PackSummary CreatePack(string token, CreatePackRequest request)
    {
        lock (gate)
        {
            if (!IsAdmin(HashToken(token))) throw new HostException(403, "Only the host owner can create packs.");
            ValidateMetadata(request.Name, request.MinecraftVersion, request.Loader, request.LoaderVersion);
            var id = Guid.NewGuid().ToString("N");
            var pack = new PackSummary(id, request.Name.Trim(), request.MinecraftVersion, request.Loader.ToLowerInvariant(), request.LoaderVersion, null, Secret());
            Directory.CreateDirectory(PackDirectory(id));
            Save(new(pack, []));
            return pack;
        }
    }

    public JoinResponse Join(string id, JoinRequest request)
    {
        lock (gate)
        {
            var state = Get(id);
            if (string.IsNullOrEmpty(request.InviteCode) || request.InviteCode.Length > 512)
                throw new HostException(404, "This invite is invalid or has been replaced. Ask the owner for a new invite.");
            var inviteHash = HashToken(request.InviteCode);
            var personal = (state.PersonalInvites ?? []).FirstOrDefault(i => EqualsHash(HashToken(i.Code), inviteHash));
            var shared = state.Pack.SharedInviteEnabled && EqualsHash(HashToken(state.Pack.InviteCode), inviteHash);
            if (!shared && (personal is null || personal.Revoked || personal.ExpiresAt <= DateTimeOffset.UtcNow || (personal.SingleUse && personal.UsedAt is not null)))
                throw new HostException(404, "This invite is invalid or has been replaced. Ask the owner for a new invite.");
            if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length > 80 || request.DisplayName.Any(char.IsControl))
                throw new HostException(400, "Enter a display name between 1 and 80 characters.");
            if (state.Members.Count(x => x.Status != "removed") >= 2000) throw new HostException(409, "This pack's member request limit has been reached. Ask the owner to decline old requests.");
            var token = Secret();
            var member = new StoredMember(Guid.NewGuid().ToString("N"), request.DisplayName.Trim(), "pending", "player", HashToken(token), DateTimeOffset.UtcNow, personal?.Label, personal?.Id);
            // Reclaimed requests keep a bounded tombstone history for removed-member status.
            var retained = state.Members.Where(m => m.Status != "removed").Concat(state.Members.Where(m => m.Status == "removed").OrderByDescending(m => m.RequestedAt).Take(2000)).ToList();
            Save(state with { Members = [.. retained, member], PersonalInvites = (state.PersonalInvites ?? []).Select(i => i.Id == personal?.Id ? i with { UsedAt = DateTimeOffset.UtcNow } : i).ToList() });
            return new(id, state.Pack.Name, member.Id, token, "pending");
        }
    }

    public AccessResponse Access(string id, string token, bool internet = false)
    {
        lock (gate)
        {
            var state = GetIncludingArchived(id, out var archived);
            var hash = HashToken(token);
            if (IsAdmin(hash)) return new(archived ? "archived" : "approved", "owner", state.Pack.Name);
            var member = state.Members.SingleOrDefault(x => EqualsHash(x.TokenHash, hash));
            if (member is null) throw new HostException(403, "Access was removed. Ask the pack owner for a new invitation.");
            if (archived) return new("archived", member.Role, state.Pack.Name);
            if (member.Status == "approved") NotePlayerAccess(id, token, "access check", internet);
            return new(member.Status, member.Role, state.Pack.Name);
        }
    }

    public List<MemberInfo> Members(string id, string token)
    {
        lock (gate)
        {
            var state = Get(id); Authorize(state, token, owner: true);
            return state.Members.Where(x => x.Status != "removed").Select(x => x.Info()).OrderBy(x => x.RequestedAt).ToList();
        }
    }

    public MemberInfo UpdateMember(string id, string memberId, string token, MemberUpdate request)
    {
        lock (gate)
        {
            var state = Get(id); Authorize(state, token, owner: true);
            if (request.Status is not ("approved" or "removed" or "pending") || request.Role is not (null or "owner" or "player"))
                throw new HostException(400, "Invalid member status or role.");
            var existing = state.Members.SingleOrDefault(x => x.Id == memberId) ?? throw new HostException(404, "Member not found.");
            var role = request.Role ?? existing.Role;
            if ((role == "owner" || existing.Role == "owner") && !IsAdmin(HashToken(token)))
                throw new HostException(403, "Only the host owner can appoint or change pack owners.");
            var updated = existing with { Status = request.Status, Role = role };
            Save(state with { Members = state.Members.Select(x => x.Id == memberId ? updated : x).ToList() });
            return updated.Info();
        }
    }

    public PackSummary RotateInvite(string id, string token)
    {
        lock (gate)
        {
            var state = Get(id); Authorize(state, token, owner: true);
            var next = state with { Pack = state.Pack with { InviteCode = Secret() } };
            Save(next);
            return Summary(next);
        }
    }

    public PackSummary SetSharedInvite(string id, string token, SharedInviteUpdate request)
    {
        lock (gate)
        {
            var state = Get(id); Authorize(state, token, owner: true);
            var next = state with { Pack = state.Pack with { SharedInviteEnabled = request.Enabled } };
            Save(next); return Summary(next);
        }
    }

    public List<PersonalInviteInfo> PersonalInvites(string id, string token)
    {
        lock (gate)
        {
            var state = Get(id); Authorize(state, token, owner: true);
            return (state.PersonalInvites ?? []).OrderByDescending(i => i.CreatedAt).ToList();
        }
    }

    public PersonalInviteInfo CreatePersonalInvite(string id, string token, CreatePersonalInviteRequest request)
    {
        lock (gate)
        {
            var state = Get(id); Authorize(state, token, owner: true);
            if (string.IsNullOrWhiteSpace(request.Label) || request.Label.Trim().Length > 80 || request.Label.Any(char.IsControl))
                throw new HostException(400, "Enter an invite label between 1 and 80 characters.");
            if (request.ExpiresAt <= DateTimeOffset.UtcNow) throw new HostException(400, "Choose an expiry time in the future.");
            var invites = state.PersonalInvites ?? [];
            bool Active(PersonalInviteInfo i) => !i.Revoked && (i.ExpiresAt is null || i.ExpiresAt > DateTimeOffset.UtcNow) && (!i.SingleUse || i.UsedAt is null);
            if (invites.Count(Active) >= 1000) throw new HostException(409, "Revoke unused personal invites before creating more.");
            var invite = new PersonalInviteInfo(Guid.NewGuid().ToString("N"), request.Label.Trim(), Secret(), request.SingleUse, request.ExpiresAt, DateTimeOffset.UtcNow);
            var retained = invites.Where(Active).Concat(invites.Where(i => !Active(i)).OrderByDescending(i => i.CreatedAt).Take(2000)).ToList();
            Save(state with { PersonalInvites = [.. retained, invite] }); return invite;
        }
    }

    public PersonalInviteInfo RevokePersonalInvite(string id, string inviteId, string token)
    {
        lock (gate)
        {
            var state = Get(id); Authorize(state, token, owner: true);
            var invites = state.PersonalInvites ?? [];
            var invite = invites.SingleOrDefault(i => i.Id == inviteId) ?? throw new HostException(404, "Personal invite not found.");
            var revoked = invite with { Revoked = true };
            Save(state with { PersonalInvites = invites.Select(i => i.Id == inviteId ? revoked : i).ToList() }); return revoked;
        }
    }

    public DeclineAllResult DeclineAll(string id, string token)
    {
        lock (gate)
        {
            var state = Get(id); Authorize(state, token, owner: true);
            var administrator = IsAdmin(HashToken(token));
            bool Decline(StoredMember member) => member.Status == "pending" && (administrator || member.Role != "owner");
            var count = state.Members.Count(Decline);
            Save(state with { Members = state.Members.Select(m => Decline(m) ? m with { Status = "removed" } : m).ToList() });
            return new(count);
        }
    }

    public ArchivePackResponse ArchivePack(string id, string token)
    {
        lock (gate)
        {
            if (!IsAdmin(HashToken(token))) throw new HostException(403, "Only the host administrator can archive packs.");
            var state = Get(id);
            var source = PackDirectory(id);
            var archive = Path.Combine(data, "archive"); Directory.CreateDirectory(archive);
            var destination = Path.Combine(archive, id);
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(archive) & FileAttributes.ReparsePoint) != 0)
                throw new HostException(409, "The pack or archive folder is linked. The host administrator must check its location.");
            var result = new ArchivePackResponse(id, state.Pack.Name, DateTimeOffset.UtcNow);
            WriteJson(Path.Combine(source, "archive-info.json"), result);
            try { Directory.Move(source, destination); }
            catch (IOException) { throw new HostException(409, "The pack could not be archived while its files are in use. Let downloads finish, then retry. Its files were kept."); }
            return result;
        }
    }

    public PackManifest ReleaseManifest(string id, string releaseId, string token)
    {
        lock (gate)
        {
            var state = Get(id); Authorize(state, token);
            if (!Guid.TryParseExact(releaseId, "N", out _)) throw new HostException(404, "Release not found.");
            var path = ReleasePath(id, releaseId);
            if (!File.Exists(path)) throw new HostException(404, "Release not found.");
            return ReadJson<PackManifest>(path);
        }
    }

    public PackManifest Manifest(string id, string token)
    {
        lock (gate)
        {
            var state = Get(id); Authorize(state, token);
            if (state.Pack.ReleaseId is null) throw new HostException(404, "The owner has not published a release yet.");
            return ReadJson<PackManifest>(ReleasePath(id, state.Pack.ReleaseId));
        }
    }

    public async Task UploadBlob(string id, string sha256, string token, Stream input, CancellationToken cancellationToken)
    {
        ValidateHash(sha256); sha256 = sha256.ToLowerInvariant();
        lock (gate) Authorize(Get(id), token, owner: true);
        var directory = Path.Combine(PackDirectory(id), "staging");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            long size = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
            {
                var buffer = new byte[128 * 1024];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    size += read;
                    if (size > MaxBlobBytes) throw new HostException(413, "A mod JAR cannot exceed 512 MiB.");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            if (size == 0 || Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() != sha256)
                throw new HostException(400, "The uploaded JAR failed its SHA-256 check. Retry the upload.");
            ValidateJar(temporary);
            lock (gate)
            {
                Authorize(Get(id), token, owner: true);
                var destination = BlobPath(id, sha256);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                // A correct immutable blob may be actively streaming to a player. Do not replace it.
                var alreadyValid = false;
                if (File.Exists(destination))
                {
                    using var existing = File.OpenRead(destination);
                    alreadyValid = EqualsHash(Convert.ToHexString(SHA256.HashData(existing)).ToLowerInvariant(), sha256);
                }
                if (!alreadyValid) File.Move(temporary, destination, overwrite: true);
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public string DownloadBlob(string id, string sha256, string token)
    {
        ValidateHash(sha256); sha256 = sha256.ToLowerInvariant();
        lock (gate)
        {
            var state = Get(id); Authorize(state, token);
            // Prior releases stay available to clients already synchronizing a complete snapshot.
            var directory = Path.Combine(PackDirectory(id), "releases");
            var known = Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.json")
                .Select(ReadJson<PackManifest>).Any(x => x.Files.Any(f => f.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase)));
            if (!known) throw new HostException(404, "This file is not part of a published release in this pack.");
            var path = BlobPath(id, sha256);
            if (!File.Exists(path)) throw new HostException(404, "This release file is missing on the host. Ask its owner to republish.");
            return path;
        }
    }

    public PackManifest Publish(string id, string token, PublishRequest request)
    {
        lock (gate)
        {
            var state = Get(id); Authorize(state, token, owner: true);
            if (request.ExpectedReleaseId != state.Pack.ReleaseId) throw new HostException(409, "Someone published a newer release. Reload the pack before publishing again.");
        }
            // Expensive content verification must not block players reading the last complete release.
            ValidateMetadata(request.Name, request.MinecraftVersion, request.Loader, request.LoaderVersion);
            if (request.Notes is null || request.Notes.Length > 4000) throw new HostException(400, "Release notes must be 4,000 characters or fewer.");
            if (request.Files is null || request.Files.Count > 2000) throw new HostException(400, "A release can contain at most 2,000 mod files.");
            long total = 0;
            var files = new List<PackFile>();
            foreach (var file in request.Files)
            {
                ValidateFile(file);
                var normalized = file with { Sha256 = file.Sha256.ToLowerInvariant(), Group = file.Group.ToLowerInvariant() };
                var blob = BlobPath(id, normalized.Sha256);
                if (!File.Exists(blob) || new FileInfo(blob).Length != normalized.Size) throw new HostException(409, $"Upload {normalized.Path} before publishing.");
                using (var input = File.OpenRead(blob))
                    if (!EqualsHash(Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant(), normalized.Sha256))
                        throw new HostException(409, $"Stored content for {normalized.Path} failed verification. Ask the host administrator to restore the blob.");
                total += normalized.Size;
                if (total > 16L * 1024 * 1024 * 1024) throw new HostException(400, "A release cannot exceed 16 GiB.");
                files.Add(normalized);
            }
            foreach (var group in files.GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
            {
                if (group.GroupBy(x => x.Group).Any(x => x.Count() > 1)) throw new HostException(400, $"{group.Key} appears more than once in the same selection.");
                if (group.Any(x => x.Group == "base") && group.Select(x => (x.Sha256, x.Size)).Distinct().Count() > 1)
                    throw new HostException(400, $"{group.Key} conflicts with a required pack mod.");
            }
            var identities = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files.DistinctBy(x => x.Sha256))
            {
                try { identities[file.Sha256] = ModFiles.ReadModIds(BlobPath(id, file.Sha256)); }
                catch (Exception exception) when (exception is InvalidDataException or JsonException or InvalidOperationException)
                { throw new HostException(400, $"The mod descriptor in {file.Path} is invalid: {exception.Message}"); }
            }
            try { ModIdentityValidation.ValidateGroups(files.Select(file => (file, identities[file.Sha256]))); }
            catch (InvalidDataException exception) { throw new HostException(400, exception.Message); }
        lock (gate)
        {
            // Authorization and optimistic concurrency are checked again after verification.
            var state = Get(id); Authorize(state, token, owner: true);
            if (request.ExpectedReleaseId != state.Pack.ReleaseId) throw new HostException(409, "Someone published a newer release. Reload the pack before publishing again.");
            var releaseId = Guid.NewGuid().ToString("N");
            var manifest = new PackManifest(id, releaseId, request.Name.Trim(), request.MinecraftVersion, request.Loader.ToLowerInvariant(), request.LoaderVersion,
                files, request.Notes, DateTimeOffset.UtcNow);
            Directory.CreateDirectory(Path.Combine(PackDirectory(id), "releases"));
            WriteJson(ReleasePath(id, releaseId), manifest);
            Save(state with { Pack = state.Pack with { Name = manifest.Name, MinecraftVersion = manifest.MinecraftVersion, Loader = manifest.Loader, LoaderVersion = manifest.LoaderVersion, ReleaseId = releaseId } });
            return manifest;
        }
    }

    private void Authorize(PackState state, string token, bool owner = false)
    {
        var hash = HashToken(token);
        if (IsAdmin(hash)) return;
        var member = state.Members.SingleOrDefault(x => EqualsHash(x.TokenHash, hash));
        if (member?.Status != "approved") throw new HostException(403, "Access is pending or has been removed. Ask the pack owner for approval.");
        if (owner && member.Role != "owner") throw new HostException(403, "Only an approved pack owner can do this.");
    }
    private bool IsAdmin(string hash) => EqualsHash(Settings.OwnerTokenHash, hash);
    private PackState Get(string id)
    {
        var state = GetIncludingArchived(id, out var archived);
        if (archived) throw new HostException(410, "This pack was archived by its owner.");
        return state;
    }
    private PackState GetIncludingArchived(string id, out bool archived)
    {
        var path = Path.Combine(PackDirectory(id), "state.json");
        archived = false;
        if (!File.Exists(path))
        {
            path = Path.Combine(data, "archive", id, "state.json");
            archived = File.Exists(path);
        }
        if (!File.Exists(path)) throw new HostException(404, "Pack not found.");
        return ReadJson<PackState>(path);
    }
    private string PackDirectory(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new HostException(404, "Pack not found.");
        return Path.Combine(data, "packs", id);
    }
    private string BlobPath(string id, string hash) => Path.Combine(PackDirectory(id), "blobs", hash);
    private string ReleasePath(string id, string releaseId) => Path.Combine(PackDirectory(id), "releases", releaseId + ".json");
    private void Save(PackState state) => WriteJson(Path.Combine(PackDirectory(state.Pack.Id), "state.json"), state);
    private static T ReadJson<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonDefaults.Options) ?? throw new InvalidDataException("Invalid Hearth host data.");
    private static void WriteJson<T>(string path, T value, bool overwrite = true)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, value, JsonDefaults.Options);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
    private static bool EqualsHash(string a, string b) => a.Length == b.Length && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
    private static void ValidateHash(string hash)
    {
        if (hash is null || !HashPattern().IsMatch(hash)) throw new HostException(400, "Invalid SHA-256 content hash.");
    }
    private static void ValidateMetadata(string name, string minecraft, string loader, string loaderVersion)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100 || name.Any(char.IsControl)) throw new HostException(400, "Pack name must be 1–100 characters.");
        if (minecraft is null || !VersionPattern().IsMatch(minecraft)) throw new HostException(400, "Choose a valid Minecraft version.");
        if (loader is null || !new[] { "fabric", "forge", "neoforge", "quilt", "vanilla" }.Contains(loader.ToLowerInvariant())) throw new HostException(400, "Choose Fabric, Forge, NeoForge, Quilt, or Vanilla.");
        if (loader.ToLowerInvariant() != "vanilla" && (loaderVersion is null || !VersionPattern().IsMatch(loaderVersion))) throw new HostException(400, "Enter a valid loader version.");
    }
    private static void ValidateFile(PackFile file)
    {
        if (file.Path is null || !file.Path.StartsWith("mods/", StringComparison.Ordinal) || file.Path.Length > 180 || !file.Path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            throw new HostException(400, "Managed files must be JARs directly inside mods/.");
        var name = file.Path[5..];
        if (name.Length == 0 || name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || name.Any(char.IsControl) || name.EndsWith(' ') || name.EndsWith('.') || name.StartsWith('.') || ReservedNamePattern().IsMatch(name))
            throw new HostException(400, "Use a safe Windows mod filename.");
        ValidateHash(file.Sha256);
        if (file.Size is <= 0 or > MaxBlobBytes) throw new HostException(400, "Invalid mod size.");
        if (file.Group is null || !new[] { "base", "voxy", "distanthorizons" }.Contains(file.Group.ToLowerInvariant())) throw new HostException(400, "Invalid optional mod group.");
    }
    private static void ValidateJar(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count is < 1 or > 100000) throw new InvalidDataException();
        }
        catch (InvalidDataException) { throw new HostException(400, "This file is not a readable JAR (ZIP archive)."); }
    }
    [GeneratedRegex("^[a-fA-F0-9]{64}$")] private static partial Regex HashPattern();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+\\-]{0,79}$")] private static partial Regex VersionPattern();
    [GeneratedRegex("^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\\.|$)", RegexOptions.IgnoreCase)] private static partial Regex ReservedNamePattern();
    public void Dispose() => processLock.Dispose();
}
