using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Windows.Controls;
using Hearth.App;
using Hearth.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class ClientIdentityChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        Directory.CreateDirectory(root);
        using var certificate = HostCertificate.LoadOrCreate(Path.Combine(root, "identity"));
        var pin = HostCertificate.GetPin(certificate);
        using var first = new IdentityFixture(certificate);
        using var moved = new IdentityFixture(certificate);
        var profile = new ProfileStore(Path.Combine(root, "profile"));
        var invite = new Invite(first.Url, "identity-pack", "invite-code", pin, IdentityFixture.Identity);
        var initial = await PackJoining.JoinAsync(profile, invite, "Friend");
        check(first.JoinRequests == 1 && initial.Status == "pending" && !initial.Reused, "A pinned invite creates the player's access request and saves the authenticated host identity");
        var pack = initial.Pack with { Terrain = TerrainMode.Voxy };
        profile.State.Packs[0] = pack; profile.Save();
        var sync = new PackSynchronizer(profile.Root);
        SyncResult installed;
        using (var api = new HearthApi(pack.HostUrl, pack.Token, certificatePin: pin)) installed = await sync.SyncAsync(api, pack);
        var options = Path.Combine(installed.InstanceDirectory, ".minecraft", "options.txt");
        var world = Path.Combine(installed.InstanceDirectory, ".minecraft", "saves", "My world", "level.dat");
        File.WriteAllText(options, "key_key.forward:87");
        Directory.CreateDirectory(Path.GetDirectoryName(world)!); File.WriteAllText(world, "personal world");

        var rebound = await PackJoining.JoinAsync(profile, invite with { HostUrl = moved.Url }, "Friend");
        check(rebound.Reused && moved.JoinRequests == 0 && moved.AccessRequests == 1,
            "A fresh address for the same pinned host reuses approved membership instead of sending another request");
        check(profile.State.Packs.Count == 1 && rebound.Pack.Token == pack.Token && rebound.Pack.MemberId == pack.MemberId
            && rebound.Pack.Terrain == TerrainMode.Voxy && rebound.Pack.InstallationUrl == first.Url,
            "Changing host address preserves the existing pack, member credential, terrain choice, and installation key");
        var reloaded = new ProfileStore(profile.Root).State.Packs.Single();
        check(reloaded.HostUrl == moved.Url && reloaded.CertificatePin == pin && reloaded.HostId == IdentityFixture.Identity
            && reloaded.InstallationUrl == first.Url, "The new endpoint and original installation identity survive encrypted profile save and reload");
        moved.Release = "release-two";
        SyncResult updated;
        using (var api = new HearthApi(reloaded.HostUrl, reloaded.Token, certificatePin: reloaded.CertificatePin)) updated = await sync.SyncAsync(api, reloaded);
        check(updated.InstanceId == installed.InstanceId && updated.InstanceDirectory == installed.InstanceDirectory && updated.DownloadedFiles == 0
            && File.ReadAllText(options) == "key_key.forward:87" && File.ReadAllText(world) == "personal world",
            "Play after an address change updates the same Prism instance without redownloading unchanged mods or disturbing personal files");
        check((await sync.VerifyInstalledAsync(reloaded.InstallationUrl, reloaded.PackId)).ReleaseId == "release-two",
            "The preserved installation still passes offline play verification after an update from the new endpoint");

        var prismPath = Path.Combine(root, "prismlauncher.exe"); File.WriteAllText(prismPath, "fixture only; never executed");
        profile.State.PrismExecutable = prismPath;
        var window = new MainWindow(profile);
        try
        {
            var status = (TextBlock)window.FindName("PackStatusText");
            var until = DateTime.UtcNow.AddSeconds(8);
            while (status.Text == "Checking access" && DateTime.UtcNow < until) await Task.Delay(20);
            check(status.Text == "Ready to play", "The actual library status client validates the pinned host and finds the preserved installed release");
            var method = typeof(MainWindow).GetMethod("IsHostAdministrator", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var admin = (Task<bool>)method.Invoke(window, [new HostConnection("test-owner", "Identity host", moved.Url, reloaded.Token, pin, IdentityFixture.Identity), CancellationToken.None])!;
            check(await admin, "The owner-permission check also uses the pinned HTTPS client");
        }
        finally { window.Close(); }

        var noPin = await PackJoining.JoinAsync(profile, invite with { HostUrl = moved.Url, CertificatePin = null }, "Friend");
        check(noPin.Pack.CertificatePin == pin && noPin.Reused, "An invite with the pin omitted cannot remove the existing saved certificate trust");
        var before = profile.State.Packs.ToArray();
        var wrongPinRejected = false;
        try { await PackJoining.JoinAsync(profile, invite with { HostUrl = moved.Url, CertificatePin = new string('a', 64) }, "Friend"); }
        catch (InvalidOperationException) { wrongPinRejected = true; }
        check(wrongPinRejected && profile.State.Packs.SequenceEqual(before), "A conflicting certificate pin is rejected without changing the saved pack");
        var wrongIdRejected = false;
        try { await PackJoining.JoinAsync(profile, invite with { HostUrl = moved.Url, HostId = "another-host" }, "Friend"); }
        catch (InvalidOperationException) { wrongIdRejected = true; }
        check(wrongIdRejected && profile.State.Packs.SequenceEqual(before), "A host identity mismatch is rejected before reusing membership");

        using var otherCertificate = HostCertificate.LoadOrCreate(Path.Combine(root, "unrelated-identity"));
        using var unrelated = new IdentityFixture(otherCertificate);
        var unrelatedResult = await PackJoining.JoinAsync(profile, invite with { HostUrl = unrelated.Url, CertificatePin = HostCertificate.GetPin(otherCertificate) }, "Friend");
        check(!unrelatedResult.Reused && unrelated.JoinRequests == 1 && unrelated.AccessRequests == 0 && profile.State.Packs.Count == 2,
            "A different certificate claiming the same host ID cannot obtain the existing player credential or reuse its installation");
        check(sync.GetInstanceId(unrelatedResult.Pack.InstallationUrl, unrelatedResult.Pack.PackId) != installed.InstanceId,
            "Unrelated hosts remain separate even if they claim matching host and pack identifiers");

        var siblingPack = new JoinedPack(moved.Url, "another-pack", "Another pack", "different-pack-credential", "different-member", CertificatePin: pin, HostId: IdentityFixture.Identity);
        profile.State.Packs.Add(siblingPack);
        profile.State.Hosts.Add(new("managed-host", "Managed host", moved.Url, "owner-credential", pin, IdentityFixture.Identity));
        profile.State.SelectedPackHost = moved.Url; profile.State.SelectedPackId = siblingPack.PackId;
        await PackJoining.JoinAsync(profile, invite, "Friend");
        var migratedSibling = profile.State.Packs.Single(p => p.PackId == siblingPack.PackId);
        check(migratedSibling.HostUrl == first.Url && migratedSibling.InstallationUrl == moved.Url && migratedSibling.Token == siblingPack.Token
            && profile.State.Hosts.Single().Url == first.Url && profile.State.SelectedPackHost == first.Url
            && profile.State.Packs.Single(p => p.CertificatePin == unrelatedResult.Pack.CertificatePin).HostUrl == unrelated.Url,
            "A verified moved address updates all same-host packs and owner connections while retaining their instance locations and isolating other certificates");

        var removed = profile.State.Packs.Single(p => p.PackId == pack.PackId && p.CertificatePin == pin);
        profile.State.Packs.Remove(removed); profile.Save();
        using var rejoinEndpoint = new IdentityFixture(certificate);
        var rejoined = await PackJoining.JoinAsync(profile, invite with { HostUrl = rejoinEndpoint.Url }, "Friend");
        check(!rejoined.Reused && rejoinEndpoint.JoinRequests == 1 && rejoined.Pack.InstallationUrl == first.Url
            && sync.GetInstanceDirectory(rejoined.Pack.InstallationUrl, rejoined.Pack.PackId) == installed.InstanceDirectory
            && File.ReadAllText(options) == "key_key.forward:87" && File.ReadAllText(world) == "personal world",
            "Removing and rejoining through a new address discovers the existing verified instance and keeps worlds and settings");

        var localProfile = new ProfileStore(Path.Combine(root, "local-owner-profile"));
        // Use only our fixture's endpoint, never the normal user's host port.
        var localUrl = "http://127.0.0.1:" + new Uri(first.Url).Port;
        var localOwner = new HostConnection("this-pc", "This PC", localUrl, "private-member-credential", HostId: IdentityFixture.Identity);
        localProfile.State.LocalHostConnectionId = localOwner.Id;
        localProfile.State.LocalHostDataDirectory = Path.Combine(root, "identity");
        localProfile.State.Hosts.Add(localOwner);
        var localPack = new JoinedPack(localUrl, "identity-pack", "Owner pack", localOwner.OwnerToken, "owner", TerrainMode.Voxy);
        localProfile.State.Packs.Add(localPack); localProfile.Save();
        check(LocalHostIdentity.FindOwnerPack(localProfile, invite with { CertificatePin = HostCertificate.GetPin(otherCertificate) }, IdentityFixture.Identity) is null,
            "A matching local host ID without the exact persisted certificate cannot obtain the local owner credential");
        var localSync = new PackSynchronizer(localProfile.Root);
        SyncResult localInstall;
        using (var api = new HearthApi(first.Url, localOwner.OwnerToken, certificatePin: pin))
            localInstall = await localSync.SyncAsync(api, localPack with { HostUrl = first.Url, CertificatePin = pin, InstanceHostUrl = localUrl });
        var localOptions = Path.Combine(localInstall.InstanceDirectory, ".minecraft", "options.txt");
        File.WriteAllText(localOptions, "owner settings");
        var requestsBefore = first.JoinRequests;
        var localRebound = await PackJoining.JoinAsync(localProfile, invite, "Owner");
        check(localRebound.Reused && first.JoinRequests == requestsBefore && localProfile.State.Packs.Count == 1
            && localRebound.Pack.Token == localOwner.OwnerToken && localRebound.Pack.MemberId == "owner"
            && localRebound.Pack.InstallationUrl == localUrl && localRebound.Pack.CertificatePin == pin,
            "Opening this PC's secure invite reuses its existing owner pack and original localhost installation");
        using (var api = new HearthApi(localRebound.Pack.HostUrl, localRebound.Pack.Token, certificatePin: pin))
        {
            var result = await localSync.SyncAsync(api, localRebound.Pack);
            check(result.InstanceId == localInstall.InstanceId && result.DownloadedFiles == 0 && File.ReadAllText(localOptions) == "owner settings",
                "Local-owner to HTTPS migration retains the Prism instance, downloaded mods, and personal settings");
        }
        localProfile.State.Packs[0] = localRebound.Pack with { CertificatePin = HostCertificate.GetPin(otherCertificate) };
        check(LocalHostIdentity.FindPinnedPack(localProfile, localOwner, localPack.PackId) is null,
            "Opening a local owned pack cannot adopt an HTTPS instance with a different certificate and a claimed matching host ID");
        localProfile.State.Packs[0] = localRebound.Pack;
        var ownerWindow = new MainWindow(localProfile);
        try
        {
            const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(MainWindow).GetField("selectedHost", privateInstance)!.SetValue(ownerWindow, localOwner);
            typeof(MainWindow).GetField("selectedPack", privateInstance)!.SetValue(ownerWindow,
                new PackSummary("identity-pack", "Owner pack", "1.21.1", "fabric", "0.16.10", "release-one", "invite-code"));
            typeof(MainWindow).GetMethod("OpenOwnedPack", privateInstance)!.Invoke(ownerWindow, null);
            var reopened = new ProfileStore(localProfile.Root).State.Packs.Single();
            check(reopened.HostUrl == localUrl && reopened.InstallationUrl == localUrl && reopened.CertificatePin is null
                && reopened.Terrain == TerrainMode.Voxy && reopened.Token == localOwner.OwnerToken
                && localSync.GetInstanceId(reopened.InstallationUrl, reopened.PackId) == localInstall.InstanceId
                && File.ReadAllText(localOptions) == "owner settings",
                "Returning from a secure invite to Open in my library reuses one owner entry and the same local Prism installation");
        }
        finally { ownerWindow.Close(); }
    }

    private sealed class IdentityFixture : IDisposable
    {
        public const string Identity = "stable-test-host";
        private const string Credential = "private-member-credential";
        private readonly WebApplication app;
        private readonly byte[] jar;
        private readonly PackFile file;
        public string Url { get; }
        public string Release = "release-one";
        public int JoinRequests;
        public int AccessRequests;
        public IdentityFixture(X509Certificate2 certificate)
        {
            using (var memory = new MemoryStream())
            {
                using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
                using (var writer = new StreamWriter(zip.CreateEntry("fixture.txt").Open())) writer.Write("client identity mod");
                jar = memory.ToArray();
            }
            file = new("mods/identity.jar", Convert.ToHexStringLower(SHA256.HashData(jar)), jar.Length, "voxy");
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)));
            builder.Logging.ClearProviders();
            app = builder.Build(); app.Run(Respond);
            Task.Run(() => app.StartAsync()).GetAwaiter().GetResult();
            Url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        }
        private async Task Respond(HttpContext context)
        {
            var path = context.Request.Path.Value ?? "";
            object result;
            if (path == "/health") result = new HealthResponse("Identity host", Identity, "1.0", DateTimeOffset.UtcNow);
            else if (path.EndsWith("/join"))
            {
                Interlocked.Increment(ref JoinRequests);
                result = new JoinResponse("identity-pack", "Identity pack", "member-one", Credential, "pending");
            }
            else if (context.Request.Headers.Authorization != "Bearer " + Credential)
            {
                context.Response.StatusCode = 401; result = new { error = "Wrong fixture member credential" };
            }
            else if (path.EndsWith("/access"))
            {
                Interlocked.Increment(ref AccessRequests); result = new AccessResponse("approved", "player", "Identity pack");
            }
            else if (path == "/owner/status") result = new { ready = true };
            else if (path.EndsWith("/manifest")) result = new PackManifest("identity-pack", Release, "Identity pack", "1.21.1", "fabric", "0.16.10", [file]);
            else if (path.Contains("/blobs/"))
            {
                context.Response.ContentType = "application/java-archive"; context.Response.ContentLength = jar.Length;
                await context.Response.Body.WriteAsync(jar); return;
            }
            else { context.Response.StatusCode = 404; result = new { error = "Unknown fixture route" }; }
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(result, JsonDefaults.Options));
        }
        public void Dispose() => Task.Run(async () => { await app.StopAsync(); await app.DisposeAsync(); }).GetAwaiter().GetResult();
    }
}
