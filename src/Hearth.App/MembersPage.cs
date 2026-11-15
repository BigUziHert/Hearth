using System.Net;
using System.Windows;
using System.Windows.Controls;
using Hearth.Core;

namespace Hearth.App;

public partial class MainWindow
{
    private List<PersonalInviteInfo> personalInvites = [];
    private bool personalInvitesSupported;

    private async Task ShowMembers() => await Run("Loading members…", async ct =>
    {
        var host = selectedHost!; var pack = selectedPack!;
        using var api = CreateOwnerApi(host);
        var members = await api.MembersAsync(pack.Id, ct);
        var admin = await IsHostAdministrator(host, ct); ownerAdminCache[host.Id] = admin;
        var latest = (await api.ListPacksAsync(ct)).FirstOrDefault(x => x.Id == pack.Id);
        if (latest != null) selectedPack = latest;
        if (admin && !IsOwnedHost(host))
        {
            try { CacheHostEndpoints(host, await api.EndpointsAsync(ct)); }
            catch (HostApiException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden) { remoteHostEndpoints.Remove(host.Id); }
        }
        try { personalInvites = await api.PersonalInvitesAsync(pack.Id, ct); personalInvitesSupported = true; }
        catch (HostApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { personalInvites = []; personalInvitesSupported = false; }
        RenderMembers(members, admin); Status("Members loaded.");
    });

    private HostLink? InviteEndpoint(HostConnection host) => GetSharingEndpoint(host);

    private void RenderMembers(List<MemberInfo> members, bool administrator)
    {
        PackEditorHeader("members"); var pack = selectedPack!; var host = selectedHost!;
        var invite = Card("Invite players", GetSharingDescription(host));
        invite.Children.Add(Text("Each person who uses an invite still needs your approval.", 12, false, "#ADBAB1"));
        if (pack.ReleaseId == null) invite.Children.Add(Text("Publish first so friends can install this pack.", 14, true, "#D8B36B"));
        else
        {
            var actions = Row();
            void AddInviteButton(string label, HostLink? link)
            {
                if (link == null) return;
                actions.Children.Add(Button(label, () => CopyText(new Invite(link.HostUrl, pack.Id, pack.InviteCode, link.CertificatePin, link.HostId).ToString(), "Invite copied.")));
            }
            if (pack.SharedInviteEnabled)
            {
                AddInviteButton("Copy internet invite", GetPublicSharingEndpoint(host));
                AddInviteButton("Copy same-network invite", GetLanSharingEndpoint(host));
            }
            else invite.Children.Add(Text("The shared invite is turned off. Create a personal invite below.", 13, false, "#ADBAB1"));
            if (InviteEndpoint(host) == null) actions.Children.Add(Button("Set up hosting", ShowHosts));
            actions.Children.Add(Button("Reset invite link", ConfirmResetInvite, true));
            invite.Children.Add(actions);
            if (personalInvitesSupported)
            {
                var shared = new CheckBox { Content = "Allow the shared invite link", IsChecked = pack.SharedInviteEnabled, Margin = new(0, 8, 0, 8) };
                shared.Click += (_, _) => _ = Run("Updating shared invite…", async ct =>
                {
                    using var api = CreateOwnerApi(host);
                    await api.SetSharedInviteAsync(pack.Id, shared.IsChecked == true, ct);
                    selectedPack = pack with { SharedInviteEnabled = shared.IsChecked == true };
                    RenderMembers(members, administrator);
                });
                invite.Children.Add(shared); RenderPersonalInvites();
            }
        }
        var pending = members.Where(x => x.Status == "pending").ToList();
        var approved = members.Where(x => x.Status == "approved").ToList();
        if (pending.Count > 0)
        {
            var section = Card($"Pending requests ({pending.Count})");
            section.Children.Add(Row(Button("Decline all", () =>
            {
                if (MessageBox.Show(this, $"Decline all {pending.Count} pending requests? Approved members keep access.", "Decline all requests", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    _ = Run("Declining pending requests…", async ct =>
                    {
                        using var api = CreateOwnerApi(host);
                        await api.DeclineAllAsync(pack.Id, ct); RenderMembers(await api.MembersAsync(pack.Id, ct), administrator); _ = RefreshOwnerPackCountsAsync();
                    });
            }, true)));
            foreach (var member in pending) AddMember(section, member);
        }
        if (approved.Count > 0)
        {
            var section = Card($"Members ({approved.Count})"); foreach (var member in approved) AddMember(section, member);
        }
        if (pending.Count == 0 && approved.Count == 0) Card("No members yet", pack.ReleaseId == null ? "Publish a release before inviting friends." : "Share an invite. Pending requests appear here.");
        void AddMember(StackPanel section, MemberInfo member)
        {
            var row = new StackPanel { Margin = new(0, 8, 0, 13) };
            row.Children.Add(Text(member.DisplayName + (member.Role == "owner" ? " · Owner" : ""), 16, true));
            if (!string.IsNullOrWhiteSpace(member.InviteLabel)) row.Children.Add(Text("Request from the invite you made for " + member.InviteLabel, 13, false, "#ADBAB1"));
            row.Children.Add(Text("Requested " + member.RequestedAt.LocalDateTime.ToString("g"), 12, false, "#ADBAB1"));
            row.Children.Add(Text("Fingerprint: " + member.Fingerprint, 12, false, "#ADBAB1"));
            var actions = Row();
            if (member.Status == "pending") actions.Children.Add(Button("Approve", () => _ = ChangeMember(member, "approved", null)));
            if (administrator && member.Status == "approved" && member.Role != "owner") actions.Children.Add(Button("Make owner", () =>
            {
                if (MessageBox.Show(this, $"Allow {member.DisplayName} to publish releases and manage members for this pack?", "Make owner", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    _ = ChangeMember(member, "approved", "owner");
            }, true));
            if (member.Status == "approved") actions.Children.Add(Button("Invite to another pack…", () => _ = InviteMemberToAnotherPack(host, member), true));
            if (administrator || member.Role != "owner") actions.Children.Add(Button(member.Status == "pending" ? "Decline" : "Remove member", () => ConfirmMemberRemoval(member), true));
            row.Children.Add(actions); section.Children.Add(row);
        }
    }

    private void ConfirmResetInvite()
    {
        if (MessageBox.Show(this, "Reset the shared invite link? The old link stops accepting new requests. Approved members keep access.", "Reset invite link", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var host = selectedHost!; var pack = selectedPack!;
        _ = Run("Resetting invite link…", async ct =>
        {
            using var api = CreateOwnerApi(host);
            selectedPack = await api.RotateInviteAsync(pack.Id, ct);
            RenderMembers(await api.MembersAsync(pack.Id, ct), ownerAdminCache.GetValueOrDefault(host.Id));
            Status("Invite link reset. Copy the new link to invite someone; approved members keep access.");
        });
    }

    private void ConfirmMemberRemoval(MemberInfo member)
    {
        var pending = member.Status == "pending";
        var dialog = new Window { Owner = this, Title = pending ? "Decline request" : "Remove member", Width = 500, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new(24) };
        panel.Children.Add(Text(pending ? $"Decline {member.DisplayName}'s request? They will not gain access to this pack." : $"Remove {member.DisplayName}'s access to this pack? Future downloads will be blocked.", 15));
        var reset = new CheckBox { Content = "Also reset the shared invite link", Margin = new(0, 12, 0, 10) }; panel.Children.Add(reset);
        panel.Children.Add(Text("Resetting stops new requests from the old shared link. Other approved members keep access.", 12, false, "#ADBAB1"));
        panel.Children.Add(Row(Button(pending ? "Decline request" : "Remove member", () => { var rotate = reset.IsChecked == true; dialog.Close(); _ = ChangeMember(member, "removed", null, rotate); }), Button("Cancel", dialog.Close, true)));
        dialog.Content = panel; dialog.ShowDialog();
    }

    private async Task ChangeMember(MemberInfo member, string status, string? role, bool resetInvite = false) => await Run("Updating pack access…", async ct =>
    {
        var host = selectedHost!; var pack = selectedPack!;
        using var api = CreateOwnerApi(host);
        await api.UpdateMemberAsync(pack.Id, member.Id, new(status, role), ct);
        if (resetInvite) selectedPack = await api.RotateInviteAsync(pack.Id, ct);
        RenderMembers(await api.MembersAsync(pack.Id, ct), ownerAdminCache.GetValueOrDefault(host.Id));
        _ = RefreshOwnerPackCountsAsync(); Status(status == "removed" ? member.Status == "pending" ? "Request declined." : "Member removed." : "Pack access updated.");
    });
}
