using System.IO;
using Hearth.Core;

namespace Hearth.App;

/// <summary>Bridges this profile's local owner instance to the same host's verified HTTPS identity.</summary>
public static class LocalHostIdentity
{
    public static JoinedPack? FindPinnedPack(ProfileStore profile, HostConnection local, string packId)
    {
        if (local.Id != profile.State.LocalHostConnectionId || string.IsNullOrWhiteSpace(local.HostId)
            || !Uri.TryCreate(local.Url, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme != "http") return null;
        var candidates = profile.State.Packs.Where(pack => pack.PackId == packId && pack.HostId == local.HostId && pack.CertificatePin is not null).ToArray();
        if (candidates.Length == 0) return null;
        var data = string.IsNullOrWhiteSpace(profile.State.LocalHostDataDirectory) ? Path.Combine(profile.Root, "Host") : profile.State.LocalHostDataDirectory;
        using var certificate = HostCertificate.LoadExisting(data);
        var pin = HostCertificate.GetPin(certificate);
        return candidates.FirstOrDefault(pack => pin.Equals(pack.CertificatePin, StringComparison.OrdinalIgnoreCase));
    }

    public static JoinedPack? FindOwnerPack(ProfileStore profile, Invite invite, string verifiedHostId)
    {
        if (invite.CertificatePin is null || string.IsNullOrWhiteSpace(verifiedHostId)) return null;
        var local = profile.State.Hosts.FirstOrDefault(host => host.Id == profile.State.LocalHostConnectionId);
        if (local is null || local.HostId != verifiedHostId || !Uri.TryCreate(local.Url, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme != "http") return null;
        var pack = profile.State.Packs.FirstOrDefault(pack => pack.PackId == invite.PackId && pack.HostUrl == local.Url
            && pack.Token == local.OwnerToken && pack.MemberId == "owner");
        if (pack is null) return null;
        var data = string.IsNullOrWhiteSpace(profile.State.LocalHostDataDirectory) ? Path.Combine(profile.Root, "Host") : profile.State.LocalHostDataDirectory;
        // A claimed host ID alone is insufficient. The certificate must also match
        // the existing protected identity of this profile's actual local host.
        using var certificate = HostCertificate.LoadExisting(data);
        return HostCertificate.GetPin(certificate).Equals(invite.CertificatePin, StringComparison.OrdinalIgnoreCase) ? pack : null;
    }
}
