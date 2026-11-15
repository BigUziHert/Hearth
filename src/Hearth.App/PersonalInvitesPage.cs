using System.Windows;
using System.Windows.Controls;
using Hearth.Core;

namespace Hearth.App;

public partial class MainWindow
{
    private void RenderPersonalInvites()
    {
        var host = selectedHost!; var pack = selectedPack!;
        var section = Card("Personal invites", "Make a labelled invite for one person. Their request shows which invite they used; approval is still required.");
        section.Children.Add(Row(Button("Create personal invite…", () => ShowPersonalInviteDialog(host, pack, ""))));
        foreach (var invite in personalInvites.OrderByDescending(x => x.CreatedAt))
        {
            var row = new StackPanel { Margin = new(0, 12, 0, 4) };
            var unavailable = invite.Revoked || invite.ExpiresAt <= DateTimeOffset.UtcNow || invite.SingleUse && invite.UsedAt != null;
            var status = invite.Revoked ? "Revoked" : invite.ExpiresAt <= DateTimeOffset.UtcNow ? "Expired" : invite.SingleUse && invite.UsedAt != null ? "Used" : "Available";
            row.Children.Add(Text(invite.Label + " · " + status, 15, true));
            row.Children.Add(Text((invite.SingleUse ? "Single use" : "Multiple uses") + (invite.ExpiresAt is {} expires ? " · Expires " + expires.LocalDateTime.ToString("g") : " · No expiry"), 12, false, "#ADBAB1"));
            var actions = Row();
            if (!unavailable && InviteEndpoint(host) is {} endpoint)
                actions.Children.Add(Button("Copy personal invite", () => CopyText(new Invite(endpoint.HostUrl, pack.Id, invite.Code, endpoint.CertificatePin, endpoint.HostId).ToString(), "Personal invite copied for " + invite.Label + "."), true));
            if (!invite.Revoked) actions.Children.Add(Button("Revoke invite", () =>
            {
                if (MessageBox.Show(this, $"Revoke the invite for {invite.Label}? It will stop accepting new requests. Existing approved members keep access.", "Revoke personal invite", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                _ = Run("Revoking personal invite…", async ct =>
                {
                    using var api = CreateOwnerApi(host);
                    await api.RevokePersonalInviteAsync(pack.Id, invite.Id, ct);
                    personalInvites = await api.PersonalInvitesAsync(pack.Id, ct);
                    RenderMembers(await api.MembersAsync(pack.Id, ct), ownerAdminCache.GetValueOrDefault(host.Id));
                });
            }, true));
            row.Children.Add(actions); section.Children.Add(row);
        }
    }

    private void ShowPersonalInviteDialog(HostConnection host, PackSummary pack, string suggestedName)
    {
        if (pack.ReleaseId == null) { Status("Publish this pack first so friends can install it."); return; }
        if (InviteEndpoint(host) == null) { Status("Set up a reachable host address before creating an invite."); return; }
        var dialog = new Window { Owner = this, Title = "Create personal invite", Width = 510, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new(24) };
        panel.Children.Add(Text("Invite someone to " + pack.Name, 20, true));
        var name = Field(panel, "Who is this invite for?", suggestedName);
        var single = new CheckBox { Content = "Allow one request only", IsChecked = true, Margin = new(0, 6, 0, 12) }; panel.Children.Add(single);
        var expiry = Select(panel, "Expires after", ["1 day", "7 days", "30 days", "Never"], "7 days");
        var create = Button("Create and copy invite", () =>
        {
            var label = name.Text.Trim(); if (label.Length == 0) { name.Focus(); return; }
            var days = expiry.SelectedIndex switch { 0 => 1, 1 => 7, 2 => 30, _ => 0 };
            var request = new CreatePersonalInviteRequest(label, single.IsChecked == true, days == 0 ? null : DateTimeOffset.UtcNow.AddDays(days));
            dialog.Close();
            _ = Run("Creating personal invite…", async ct =>
            {
                using var api = CreateOwnerApi(host);
                var created = await api.CreatePersonalInviteAsync(pack.Id, request, ct);
                if (InviteEndpoint(host) is {} endpoint)
                    await ClipboardService.SetTextAsync(new Invite(endpoint.HostUrl, pack.Id, created.Code, endpoint.CertificatePin, endpoint.HostId).ToString(), Status,
                        "Personal invite copied for " + label + ". Send it to them; their request still needs approval.");
                if (selectedPack?.Id == pack.Id && selectedHost?.Id == host.Id)
                {
                    personalInvites = await api.PersonalInvitesAsync(pack.Id, ct);
                    RenderMembers(await api.MembersAsync(pack.Id, ct), ownerAdminCache.GetValueOrDefault(host.Id));
                }
            });
        });
        panel.Children.Add(Row(create, Button("Cancel", dialog.Close, true)));
        dialog.Content = panel; dialog.ShowDialog();
    }

    private async Task InviteMemberToAnotherPack(HostConnection host, MemberInfo member) => await Run("Loading packs…", async ct =>
    {
        var (packs, _) = await QueryOwnerPacks(host, ct);
        var choices = packs.Where(x => x.Id != selectedPack?.Id && x.ReleaseId != null).ToList();
        if (choices.Count == 0) { Status("Publish another pack on this host before inviting someone to it."); return; }
        var dialog = new Window { Owner = this, Title = "Invite to another pack", Width = 480, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new(24) };
        panel.Children.Add(Text("Invite " + member.DisplayName, 20, true));
        panel.Children.Add(Text("They will receive a personal invite and request access to the selected pack.", 13, false, "#ADBAB1"));
        var picker = new ComboBox { ItemsSource = choices, DisplayMemberPath = "Name", SelectedIndex = 0 }; panel.Children.Add(picker);
        PackSummary? target = null;
        panel.Children.Add(Row(Button("Continue", () => { target = picker.SelectedItem as PackSummary; dialog.Close(); }), Button("Cancel", dialog.Close, true)));
        dialog.Content = panel; dialog.ShowDialog();
        if (target != null)
        {
            var destination = ownerPackConnections.GetValueOrDefault(host.Id + "/" + target.Id, host);
            // Run finishes before the next dialog's create action begins another operation.
            _ = Dispatcher.BeginInvoke(new Action(() => ShowPersonalInviteDialog(destination, target, member.DisplayName)));
        }
    });

    private async Task ShowPeople(HostConnection host) => await Run("Loading people…", async ct =>
    {
        var (packs, _) = await QueryOwnerPacks(host, ct);
        var people = new List<(PackSummary Pack, MemberInfo Member)>();
        foreach (var pack in packs)
        {
            var credential = ownerPackConnections.GetValueOrDefault(host.Id + "/" + pack.Id, host);
            using var api = CreateOwnerApi(credential);
            people.AddRange((await api.MembersAsync(pack.Id, ct)).Select(member => (pack, member)));
        }
        PageStart("publish", "People · " + host.Name, "");
        Page.Children.Add(Row(Button("Back to Share packs", ShowOwnerPackList, true)));
        if (people.Count == 0) { Card("No people yet", "Create an invite from a published pack's Members tab."); return; }
        foreach (var person in people.GroupBy(x => (Name: x.Member.DisplayName.Trim().ToUpperInvariant(), Label: (x.Member.InviteLabel ?? "").Trim().ToUpperInvariant())).OrderBy(x => x.Key.Name))
        {
            var first = person.First().Member;
            var card = Card(first.DisplayName, string.IsNullOrWhiteSpace(first.InviteLabel) ? "Shared invite" : "Invite made for " + first.InviteLabel);
            foreach (var entry in person) card.Children.Add(Text(entry.Pack.Name + " · " + entry.Member.Status + (entry.Member.Role == "owner" ? " · Owner" : ""), 14));
            if (person.Any(x => x.Member.Status == "approved")) card.Children.Add(Row(Button("Invite to another pack…", () => _ = InviteMemberToAnotherPack(host, first), true)));
        }
    });
}
