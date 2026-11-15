using System.IO;
using System.Security.Cryptography;
using System.Text;
using Hearth.Core;

namespace Hearth.App;

internal static class HostConnectionMigration
{
    internal static bool NormalizeDelegated(ProfileStore profile)
    {
        var original = profile.State.Hosts.ToList();
        var normalized = new List<HostConnection>();
        foreach (var host in original)
        {
            var memberPacks = profile.State.Packs.Where(pack => pack.MemberId != "owner" && pack.Token == host.OwnerToken
                && SameHost(host, pack)).ToList();
            var delegated = host.Id != profile.State.LocalHostConnectionId && (host.IsDelegated || memberPacks.Count > 0);
            if (!delegated) { normalized.Add(host); continue; }
            var replacement = host.IsDelegated ? host : host with
            {
                IsDelegated = true,
                Name = "Shared with me · " + new Uri(host.Url).Authority,
                HostId = host.HostId ?? memberPacks.Select(pack => pack.HostId).FirstOrDefault(id => id != null)
            };
            // Retain old draft files and give the stable host/pack key a copy before removing
            // redundant local connection IDs. Existing current drafts always take precedence.
            var canonical = normalized.FirstOrDefault(existing => existing.IsDelegated && SameGroup(existing, replacement));
            var draftHost = canonical ?? replacement;
            foreach (var pack in memberPacks)
                PreserveLegacyDraft(profile.Root, host.Id, draftHost.HostId ?? draftHost.Id, pack.PackId);
            if (canonical == null) normalized.Add(replacement);
        }
        if (original.SequenceEqual(normalized)) return false;
        profile.State.Hosts = normalized;
        try { profile.Save(); }
        catch { profile.State.Hosts = original; throw; }
        return true;
    }

    private static bool SameHost(HostConnection host, JoinedPack pack) =>
        string.Equals(host.CertificatePin, pack.CertificatePin, StringComparison.OrdinalIgnoreCase)
        && (host.HostId != null && pack.HostId != null && host.CertificatePin != null
            ? host.HostId == pack.HostId : host.Url == pack.HostUrl);

    private static bool SameGroup(HostConnection left, HostConnection right) =>
        string.Equals(left.CertificatePin, right.CertificatePin, StringComparison.OrdinalIgnoreCase)
        && (left.HostId != null && right.HostId != null && left.CertificatePin != null
            ? left.HostId == right.HostId : left.Url == right.Url);

    private static void PreserveLegacyDraft(string root, string oldHostId, string hostId, string packId)
    {
        if (oldHostId == hostId) return;
        static string Key(string host, string pack) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(host + "\n" + pack))) + ".json";
        var source = Path.Combine(root, "Drafts", Key(oldHostId, packId));
        var destination = Path.Combine(root, "Drafts", Key(hostId, packId));
        if (File.Exists(source) && !File.Exists(destination)) File.Copy(source, destination, false);
    }
}
