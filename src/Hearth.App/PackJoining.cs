using System.Net;
using Hearth.Core;

namespace Hearth.App;

public sealed record PackJoinResult(JoinedPack Pack, string Status, bool Reused);

/// <summary>Verifies an invite before reusing saved credentials or an installation.</summary>
public static class PackJoining
{
    public static async Task<PackJoinResult> JoinAsync(ProfileStore profile, Invite invite, string displayName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("Enter your name so the owner knows who to approve.");
        var sameAddress = profile.State.Packs.FirstOrDefault(pack => pack.HostUrl == invite.HostUrl && pack.PackId == invite.PackId);
        var destination = new Uri(invite.HostUrl);
        var local = profile.State.Hosts.FirstOrDefault(host => host.Id == profile.State.LocalHostConnectionId);
        if (destination.IsLoopback && destination.Scheme == "http"
            && (local?.Url == invite.HostUrl || sameAddress != null && sameAddress.Token == profile.State.LocalHostOwnerToken && sameAddress.Token.Length > 0)
            && (System.Windows.Application.Current as App)?.LocalHost?.IsRunning != true)
            throw new InvalidOperationException("Hosting is off on this PC. Open Share packs and enable hosting before reconnecting this local pack.");
        if (sameAddress?.CertificatePin is { } savedPin && invite.CertificatePin is { } invitePin
            && !savedPin.Equals(invitePin, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This invite has a different security identity from your saved host. Your existing connection was kept. Ask the owner to check the invite.");

        // An existing address keeps its established trust policy. In particular,
        // a new invite cannot downgrade a publicly trusted connection to an
        // arbitrary self-signed certificate before we send its saved credential.
        var pin = sameAddress is null ? invite.CertificatePin : sameAddress.CertificatePin;
        using var api = new HearthApi(invite.HostUrl, certificatePin: pin);
        var health = await api.HealthAsync(ct);
        if (string.IsNullOrWhiteSpace(health.HostId)
            || (invite.HostId is not null && invite.HostId != health.HostId)
            || (sameAddress?.HostId is not null && sameAddress.HostId != health.HostId))
            throw new InvalidOperationException("The host answering this address does not match the invitation or your saved host. Ask the owner for a current invite.");

        var existing = sameAddress ?? profile.State.Packs.FirstOrDefault(pack => pack.PackId == invite.PackId
            && pin is not null && pack.CertificatePin is not null
            && pack.CertificatePin.Equals(pin, StringComparison.OrdinalIgnoreCase)
            && pack.HostId == health.HostId)
            ?? LocalHostIdentity.FindOwnerPack(profile, invite, health.HostId);
        JoinedPack joined;
        string status;
        var reused = false;
        if (existing is not null)
        {
            using var current = new HearthApi(invite.HostUrl, existing.Token, certificatePin: pin);
            try
            {
                var access = await current.AccessAsync(existing.PackId, ct);
                if (access.Status is "approved" or "pending")
                {
                    joined = existing with { HostUrl = invite.HostUrl, HostId = health.HostId, CertificatePin = pin, InstanceHostUrl = existing.InstallationUrl };
                    status = access.Status;
                    reused = true;
                    return Save(profile, existing, joined, displayName, status, reused);
                }
            }
            catch (HostApiException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) { }
        }

        var response = await api.JoinAsync(invite.PackId, new(invite.Code, displayName.Trim()), ct);
        if (response.PackId != invite.PackId) throw new InvalidOperationException("The host returned a different pack. Your library has not changed.");
        joined = new(invite.HostUrl, response.PackId, response.Name, response.Token, response.MemberId,
            existing?.Terrain ?? TerrainMode.Off, pin, existing?.InstallationUrl
                ?? profile.State.RememberedInstances.FirstOrDefault(x => x.HostId == health.HostId && x.PackId == invite.PackId && pin != null && x.CertificatePin.Equals(pin, StringComparison.OrdinalIgnoreCase))?.InstallationUrl
                ?? await Task.Run(() => new PackSynchronizer(profile.Root, profile.State.InstanceDirectory).FindInstallationUrl(health.HostId, invite.PackId, pin), ct), health.HostId);
        status = response.Status;
        return Save(profile, existing, joined, displayName, status, reused);
    }

    private static PackJoinResult Save(ProfileStore profile, JoinedPack? existing, JoinedPack joined, string name, string status, bool reused)
    {
        var originalPacks = profile.State.Packs.ToList();
        var originalName = profile.State.DisplayName;
        var originalHosts = profile.State.Hosts.ToList();
        var selectedAddress = profile.State.SelectedPackHost;
        var index = existing is null ? -1 : profile.State.Packs.IndexOf(existing);
        if (index < 0) profile.State.Packs.Add(joined); else profile.State.Packs[index] = joined;
        if (joined.HostId != null && joined.CertificatePin != null)
        {
            bool SameHost(string? id, string? pin) => id == joined.HostId && string.Equals(pin, joined.CertificatePin, StringComparison.OrdinalIgnoreCase);
            profile.State.Packs = profile.State.Packs.Select(p => SameHost(p.HostId, p.CertificatePin)
                ? p with { HostUrl = joined.HostUrl, InstanceHostUrl = p.InstallationUrl } : p).ToList();
            profile.State.Hosts = profile.State.Hosts.Select(h => SameHost(h.HostId, h.CertificatePin) ? h with { Url = joined.HostUrl } : h).ToList();
            if (originalPacks.Any(p => p.HostUrl == selectedAddress && SameHost(p.HostId, p.CertificatePin))) profile.State.SelectedPackHost = joined.HostUrl;
        }
        profile.State.DisplayName = name.Trim();
        try { profile.Save(); }
        catch { profile.State.Packs = originalPacks; profile.State.Hosts = originalHosts; profile.State.SelectedPackHost = selectedAddress; profile.State.DisplayName = originalName; throw; }
        return new(joined, status, reused);
    }
}
