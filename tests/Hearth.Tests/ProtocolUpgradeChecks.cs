using System.Net;
using System.Text.Json;
using Hearth.Core;

internal static class ProtocolUpgradeChecks
{
    public static async Task RunAsync(HearthApi owner, string hostUrl, string hostData, string pin, Action<bool, string> check)
    {
        var legacySummary = JsonSerializer.Deserialize<PackSummary>("""{"id":"legacy","name":"Legacy pack","minecraftVersion":"1.21.1","loader":"fabric","loaderVersion":"0.16.10","releaseId":null,"inviteCode":"legacy-code"}""", JsonDefaults.Options)!;
        check(legacySummary.SharedInviteEnabled && legacySummary.PendingCount == 0, "Older pack metadata keeps shared invites enabled when additive fields are absent");
        var statusOnly = JsonSerializer.Deserialize<MemberUpdate>("""{"status":"approved"}""", JsonDefaults.Options)!;
        check(statusOnly.Role is null, "Older status-only PATCH requests do not inject a replacement role");
        using (var timeoutClient = new HttpClient(new WaitingHandler()) { Timeout = TimeSpan.FromMilliseconds(100) })
        using (var timeoutApi = new HearthApi(hostUrl, client: timeoutClient, hostName: "Fixture host"))
        {
            try { await timeoutApi.HealthAsync(); throw new InvalidOperationException("Timed-out request unexpectedly succeeded"); }
            catch (HttpRequestException exception) { check(exception.Message.StartsWith("Can't reach Fixture host.", StringComparison.Ordinal), "A stalled request reports the host's friendly name"); }
        }
        using (var cancelClient = new HttpClient(new WaitingHandler()))
        using (var cancelApi = new HearthApi(hostUrl, client: cancelClient))
        using (var cancellation = new CancellationTokenSource(100))
        {
            try { await cancelApi.HealthAsync(cancellation.Token); throw new InvalidOperationException("Canceled request unexpectedly succeeded"); }
            catch (OperationCanceledException) { check(cancellation.IsCancellationRequested, "Cancel remains cancellation instead of an offline-host error"); }
        }
        using var anonymous = new HearthApi(hostUrl);
        var pack = await owner.CreatePackAsync(new("Protocol regression", "1.21.1", "fabric", "0.16.10"));
        var second = await owner.CreatePackAsync(new("Other private pack", "1.21.1", "fabric", "0.16.10"));
        var original = await owner.PublishAsync(pack.Id, new(pack.Name, pack.MinecraftVersion, pack.Loader, pack.LoaderVersion, [], "First notes", null));
        var latest = await owner.PublishAsync(pack.Id, new(pack.Name, pack.MinecraftVersion, pack.Loader, pack.LoaderVersion, [], "Second notes", original.ReleaseId));
        var delegated = await anonymous.JoinAsync(pack.Id, new(pack.InviteCode, "Delegate"));
        await owner.UpdateMemberAsync(pack.Id, delegated.MemberId, new("approved", "owner"));
        var kept = await owner.UpdateMemberAsync(pack.Id, delegated.MemberId, new("approved"));
        check(kept.Role == "owner", "PATCH without a role preserves an existing pack owner's role");
        using var delegatedApi = new HearthApi(hostUrl, delegated.Token);
        check(await owner.IsAdministratorAsync() && !await delegatedApi.IsAdministratorAsync(), "Administrator discovery distinguishes host administrators from delegated pack owners");
        await Denied(() => delegatedApi.ArchivePackAsync(pack.Id), check, "Delegated pack owner cannot archive a host administrator's pack");
        await Denied(() => delegatedApi.EndpointsAsync(), check, "Advertised host addresses require administrator access");

        var personal = await owner.CreatePersonalInviteAsync(pack.Id, new("Alex", true, DateTimeOffset.UtcNow.AddDays(1)));
        var joined = await anonymous.JoinAsync(pack.Id, new(personal.Code, "Different display name"));
        check((await owner.MembersAsync(pack.Id)).Single(m => m.Id == joined.MemberId).InviteLabel == "Alex",
            "Personal invite records the owner's label independently of the request's display name");
        await Rejected(() => anonymous.JoinAsync(pack.Id, new(personal.Code, "Forwarded")), HttpStatusCode.NotFound, check, "Single-use personal invite cannot create a second access request");
        check((await owner.ListPacksAsync()).Single(p => p.Id == pack.Id).PendingCount == 1, "Owner list reports pending requests without downloading member lists");
        using var player = new HearthApi(hostUrl, joined.Token);
        await Denied(() => player.ManifestAsync(pack.Id), check, "A personal invite still requires approval before metadata download");
        await Denied(() => player.PersonalInvitesAsync(pack.Id), check, "Players cannot read personal invite codes");
        await owner.UpdateMemberAsync(pack.Id, joined.MemberId, new("approved"));
        check((await player.ReleaseManifestAsync(pack.Id, original.ReleaseId)).Notes == "First notes"
            && (await player.ManifestAsync(pack.Id)).ReleaseId == latest.ReleaseId, "Authorized historical manifests preserve draft baselines without changing the current release");
        await Denied(() => player.ReleaseManifestAsync(second.Id, original.ReleaseId), check, "Historical release access remains scoped to its private pack");

        var sharedOff = await owner.SetSharedInviteAsync(pack.Id, false);
        check(!sharedOff.SharedInviteEnabled, "Owners can turn off a pack's shared invite link");
        await Rejected(() => anonymous.JoinAsync(pack.Id, new(pack.InviteCode, "Shared disabled")), HttpStatusCode.NotFound, check, "Disabled shared invite rejects old-client code requests");
        var reusable = await owner.CreatePersonalInviteAsync(pack.Id, new("Group invite", false));
        var firstPending = await anonymous.JoinAsync(pack.Id, new(reusable.Code, "One"));
        await anonymous.JoinAsync(pack.Id, new(reusable.Code, "Two"));
        check((await owner.ListPacksAsync()).Single(p => p.Id == pack.Id).PendingCount == 2, "Reusable personal invites work while the shared invite is disabled");
        await owner.RevokePersonalInviteAsync(pack.Id, reusable.Id);
        await Rejected(() => anonymous.JoinAsync(pack.Id, new(reusable.Code, "Revoked")), HttpStatusCode.NotFound, check, "Revoking a personal invite blocks subsequent requests");
        check((await owner.PersonalInvitesAsync(pack.Id)).Single(i => i.Id == reusable.Id).Revoked, "Revoked personal invites remain visible in owner history");
        await Rejected(() => owner.CreatePersonalInviteAsync(pack.Id, new("Already expired", true, DateTimeOffset.UtcNow.AddMinutes(-1))), HttpStatusCode.BadRequest, check, "Invite creation rejects an expiry in the past");
        var expiring = await owner.CreatePersonalInviteAsync(pack.Id, new("Expires", false, DateTimeOffset.UtcNow.AddSeconds(1)));
        await Task.Delay(1100);
        await Rejected(() => anonymous.JoinAsync(pack.Id, new(expiring.Code, "Too late")), HttpStatusCode.NotFound, check, "Expired personal invite cannot create a request");
        var declined = await owner.DeclineAllAsync(pack.Id);
        check(declined.Count == 2 && (await owner.ListPacksAsync()).Single(p => p.Id == pack.Id).PendingCount == 0,
            "Decline all clears pending counts while leaving approved members untouched");
        using var declinedPlayer = new HearthApi(hostUrl, firstPending.Token);
        check((await declinedPlayer.AccessAsync(pack.Id)).Status == "removed", "Declined and removed credentials get a distinct access status");
        check((await player.AccessAsync(pack.Id)).Status == "approved", "Declining pending requests preserves already approved access");
        await owner.SetSharedInviteAsync(pack.Id, true);
        var rotated = await owner.RotateInviteAsync(pack.Id);
        await Rejected(() => anonymous.JoinAsync(pack.Id, new(pack.InviteCode, "Old shared link")), HttpStatusCode.NotFound, check, "Resetting the shared link invalidates its old code");
        await anonymous.JoinAsync(pack.Id, new(rotated.InviteCode, "New shared link"));
        check((await player.AccessAsync(pack.Id)).Status == "approved", "Resetting an invite keeps previously approved members authorized");
        await Denied(() => player.RotateInviteAsync(pack.Id), check, "A player cannot reset an invite");
        await Denied(() => player.CreatePersonalInviteAsync(pack.Id, new("Unauthorized")), check, "A player cannot create personal invites");
        await Denied(() => delegatedApi.CreatePersonalInviteAsync(second.Id, new("Other pack")), check, "A delegated owner cannot invite users to another private pack");

        var endpointPath = Path.Combine(hostData, "advertised-endpoints.json");
        var previousEndpoints = File.Exists(endpointPath) ? await File.ReadAllBytesAsync(endpointPath) : null;
        try
        {
            var hostId = (await owner.HealthAsync()).HostId;
            var endpoint = new HostEndpoints(hostId, pin, "https://my-hearth.example:17846", ["https://192.168.1.20:17843"], DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(endpointPath, JsonSerializer.Serialize(endpoint, JsonDefaults.Options));
            var fetched = await owner.EndpointsAsync();
            check(fetched.InternetAddress == endpoint.InternetAddress && fetched.HomeNetworkAddresses.SequenceEqual(endpoint.HomeNetworkAddresses)
                && fetched.CertificatePin == pin, "Remote managers receive both advertised DNS Internet and home-network endpoints with their pin");
            await File.WriteAllTextAsync(endpointPath, JsonSerializer.Serialize(endpoint with { HostId = "unrelated-host" }, JsonDefaults.Options));
            check((await owner.EndpointsAsync()).InternetAddress is null, "An advertised endpoint for a different host identity is not exposed");
        }
        finally
        {
            if (previousEndpoints is null) File.Delete(endpointPath); else await File.WriteAllBytesAsync(endpointPath, previousEndpoints);
        }

        var keptFile = Path.Combine(hostData, "packs", pack.Id, "archive-preservation.fixture");
        await File.WriteAllTextAsync(keptFile, "Keep all original pack bytes");
        var archived = await owner.ArchivePackAsync(pack.Id);
        check(archived.PackId == pack.Id && !(await owner.ListPacksAsync()).Any(p => p.Id == pack.Id), "Archived packs disappear from active owner lists");
        check(await File.ReadAllTextAsync(Path.Combine(hostData, "archive", pack.Id, "archive-preservation.fixture")) == "Keep all original pack bytes"
            && !Directory.Exists(Path.Combine(hostData, "packs", pack.Id)), "Archiving moves the entire pack into an archive without deleting its files");
        check((await player.AccessAsync(pack.Id)).Status == "archived", "Existing members see an explicit archived-pack status");
        await Rejected(() => player.ManifestAsync(pack.Id), HttpStatusCode.Gone, check, "Archived packs stop serving release metadata");
        await Rejected(() => anonymous.JoinAsync(pack.Id, new(rotated.InviteCode, "Archived")), HttpStatusCode.Gone, check, "Archived invites cannot create new member requests");
        await Denied(() => anonymous.AccessAsync(pack.Id), check, "Archiving does not reveal member status to anonymous requests");
        await owner.ArchivePackAsync(second.Id);
    }

    private sealed class WaitingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("A waiting request must end by cancellation.");
        }
    }

    private static Task Denied(Func<Task> action, Action<bool, string> check, string description) => Rejected(action, null, check, description);
    private static async Task Rejected(Func<Task> action, HttpStatusCode? expected, Action<bool, string> check, string description)
    {
        try { await action(); throw new InvalidOperationException(description + " was unexpectedly allowed"); }
        catch (HostApiException ex) when (expected is null ? ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized : ex.StatusCode == expected)
        { check(true, description); }
    }
}
