using System.Net;
using System.Security.Cryptography;
using Hearth.Core;
using Hearth.Host;

internal static class InternetAccessChecks
{
    public static void Run(string directory, Action<bool, string> check)
    {
        check(HostNetworkAddress.ShouldConfirmInternet(true, IPAddress.Parse("8.8.8.8"))
            && HostNetworkAddress.ShouldConfirmInternet(true, IPAddress.Parse("::ffff:8.8.8.8"))
            && !HostNetworkAddress.ShouldConfirmInternet(false, IPAddress.Parse("8.8.8.8"))
            && !HostNetworkAddress.ShouldConfirmInternet(true, IPAddress.Loopback)
            && !HostNetworkAddress.ShouldConfirmInternet(true, IPAddress.Parse("192.168.1.42")),
            "Internet reachability evidence requires both TLS and a direct public peer; public HTTP and local HTTPS never qualify");
        check(HostNetworkAddress.IsPublicIPv4(IPAddress.Parse("::ffff:8.8.8.8"))
            && !HostNetworkAddress.IsPublicIPv4(IPAddress.Parse("::ffff:127.0.0.1"))
            && !HostNetworkAddress.IsPublicIPv4(IPAddress.Parse("::ffff:192.168.1.42"))
            && !HostNetworkAddress.IsPublicIPv4(IPAddress.Parse("100.64.0.1"))
            && !HostNetworkAddress.IsPublicIPv4(IPAddress.Parse("::1")) && !HostNetworkAddress.IsPublicIPv4(null),
            "Direct Internet peer classification accepts public mapped IPv4 and rejects LAN, loopback, CGNAT, and missing peers");
        Directory.CreateDirectory(directory);
        var owner = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var keyFile = Path.Combine(directory, "fixture-owner.key"); File.WriteAllText(keyFile, owner);
        var arguments = new HostArguments(directory, true, keyFile, 17842, "127.0.0.1");
        HostStore.Initialize(arguments); File.Delete(keyFile);
        using (var store = new HostStore(arguments with { Initialize = false, OwnerTokenFile = null }))
        {
            var pack = store.CreatePack(owner, new("Outside connection fixture", "1.21.1", "fabric", "0.16.10"));
            var member = store.Join(pack.Id, new(pack.InviteCode, "Approved friend"));
            store.NotePlayerAccess(pack.Id, member.Token, "manifest", internet: true);
            store.NotePlayerAccess(pack.Id, "invalid-credential", "manifest", internet: true);
            store.NotePlayerAccess(pack.Id, owner, "manifest", internet: true);
            check(store.Status(owner).LastInternetPlayerAccessAt == null && store.Status(owner).SuccessfulPlayerRequests == 0,
                "Pending, invalid, and host-owner credentials cannot masquerade as an approved outside player connection");
            store.UpdateMember(pack.Id, member.MemberId, owner, new("approved"));
            store.Access(pack.Id, member.Token);
            check(store.Status(owner).LastPlayerAccessAt != null && store.Status(owner).LastInternetPlayerAccessAt == null,
                "An approved local player request records activity without confirming Internet access");
            store.Access(pack.Id, member.Token, internet: true);
            var confirmed = store.Status(owner);
            check(confirmed.LastInternetPlayerAccessAt != null && confirmed.LastInternetPlayerName == "Approved friend",
                "An approved outside player request records its timestamp and display name");
            store.NotePlayerAccess(pack.Id, member.Token, "download");
            check(store.Status(owner).LastInternetPlayerAccessAt == confirmed.LastInternetPlayerAccessAt,
                "Later LAN requests do not overwrite the last confirmed outside connection");
            store.UpdateMember(pack.Id, member.MemberId, owner, new("removed"));
            store.NotePlayerAccess(pack.Id, member.Token, "manifest", internet: true);
            check(store.Status(owner).LastInternetPlayerAccessAt == confirmed.LastInternetPlayerAccessAt,
                "Removed players cannot refresh Internet reachability evidence");
            try { store.Status(member.Token); throw new InvalidOperationException("Player read host status"); }
            catch (HostException error) { check(error.Status == 403, "Outside-connection evidence remains visible only to the host administrator"); }
        }
        using var reopened = new HostStore(arguments with { Initialize = false, OwnerTokenFile = null });
        check(reopened.Status(owner).LastInternetPlayerAccessAt == null && reopened.Status(owner).LastPlayerAccessAt == null,
            "Outside-connection evidence resets when hosting restarts instead of claiming old reachability");
    }
}
