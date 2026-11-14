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
            existing?.Terrain ?? TerrainMode.Off, pin, existing?.InstallationUrl, health.HostId);
        status = response.Status;
        return Save(profile, existing, joined, displayName, status, reused);
    }

    private static PackJoinResult Save(ProfileStore profile, JoinedPack? existing, JoinedPack joined, string name, string status, bool reused)
    {
        var originalPacks = profile.State.Packs.ToList();
        var originalName = profile.State.DisplayName;
        var index = existing is null ? -1 : profile.State.Packs.IndexOf(existing);
        if (index < 0) profile.State.Packs.Add(joined); else profile.State.Packs[index] = joined;
        profile.State.DisplayName = name.Trim();
        try { profile.Save(); }
        catch { profile.State.Packs = originalPacks; profile.State.DisplayName = originalName; throw; }
        return new(joined, status, reused);
    }
}
