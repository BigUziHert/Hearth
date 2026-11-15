using System.Reflection;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Hearth.App;
using Hearth.Core;

internal static class HostingActionChecks
{
    public static void Run(string root, Action<bool, string> check)
    {
        CheckLegacyDelegatedConnections(Path.Combine(root, "legacy"), check);
        var profile = new ProfileStore(root);
        profile.State.LocalHostConnectionId = "local-actions-fixture";
        profile.State.Hosts.Add(new("local-actions-fixture", "This PC", "http://127.0.0.1:17842", "fixture-owner-key"));
        profile.State.Hosts.Add(new("delegated-actions-fixture", "Shared with me · Friends", "http://127.0.0.1:19121", "fixture-member-key", HostId: "friends", IsDelegated: true));
        var window = new MainWindow(profile);
        try
        {
            typeof(MainWindow).GetMethod("ShowHosts", BindingFlags.Instance | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null)!.Invoke(window, null);
            var buttons = Descendants(window).OfType<Button>().ToList();
            var enable = buttons.Single(x => Equals(x.Content, "Enable hosting"));
            var retry = buttons.Single(x => Equals(x.Content, "Retry connection setup"));
            var stop = buttons.Single(x => Equals(x.Content, "Disable hosting"));
            check(((TextBlock)window.FindName("PageTitle")).Text == "Share packs"
                && Descendants(window).OfType<Expander>().All(x => !Descendants(x).Contains(enable)),
                "One Share packs page exposes the This PC hosting controls outside the connection expander");
            check(buttons.Count(x => Equals(x.Content, "Copy owner key")) == 1
                && buttons.Any(x => Equals(x.Content, "Rename"))
                && !Descendants(window).OfType<TextBlock>().Any(x => x.Text.Contains("Shared with me · Shared with me", StringComparison.Ordinal)),
                "A delegated host can be renamed, appears once, and never labels its member credential as an owner key");
            check(Visible(enable) && !Visible(retry) && !Visible(stop)
                && !buttons.Any(x => Equals(x.Content, "Hosting enabled") || Equals(x.Content, "Retry hosting")),
                "Disabled local hosting has one startup action and no duplicate repair or enabled-state button");

            MainWindow.UpdateHostingActions(true, true, null, enable, retry, stop);
            check(!Visible(enable) && Visible(retry) && Visible(stop),
                "A running host with unfinished sharing offers one connection repair action plus Disable hosting");
            var lanOnly = new HostNetworkResult(["https://192.168.1.42:17843"], null, true, false, "Home network only");
            MainWindow.UpdateHostingActions(true, true, lanOnly, enable, retry, stop);
            check(!Visible(enable) && Visible(retry) && Visible(stop),
                "LAN-only hosting retains the action needed to retry Internet connection setup");
            var blocked = lanOnly with { FirewallReady = false };
            MainWindow.UpdateHostingActions(true, true, blocked, enable, retry, stop);
            check(Visible(retry) && !Visible(enable),
                "Running loopback hosting can repair missing Windows permission without a second Enable hosting action");
            var ready = lanOnly with { PublicAddress = "https://8.8.4.4:17843", RouterMapped = true };
            MainWindow.UpdateHostingActions(true, true, ready, enable, retry, stop);
            check(!Visible(enable) && !Visible(retry) && Visible(stop),
                "Configured sharing shows Disable hosting and does not offer an unnecessary restart");
            MainWindow.UpdateHostingActions(false, true, ready, enable, retry, stop);
            check(Visible(enable) && !Visible(retry) && Visible(stop),
                "A stopped enabled host has one start action and can still disable automatic resume");
            MainWindow.UpdateHostingActions(false, false, ready, enable, retry, stop);
            check(Visible(enable) && !Visible(retry) && !Visible(stop),
                "Disabling hosting hides repair and stop actions even if an old network result remains");
        }
        finally { window.Close(); }
    }

    private static void CheckLegacyDelegatedConnections(string root, Action<bool, string> check)
    {
        var profile = new ProfileStore(root);
        var pin = new string('a', 64);
        var rootOwner = new HostConnection("local-root", "This PC", "http://127.0.0.1:17842", "owner-key", HostId: "local");
        var remoteOwner = new HostConnection("remote-root", "My server", "https://admin.example:17843", "administrator-key", pin, "admin");
        var first = new HostConnection("old-pack-a", "Pack A", "https://friends.example:17843", "member-a", pin, "friends");
        var second = new HostConnection("old-pack-b", "Pack B", "https://changed.example:17843", "member-b", pin, "friends");
        profile.State.LocalHostConnectionId = rootOwner.Id;
        profile.State.Hosts.AddRange([rootOwner, remoteOwner, first, second]);
        profile.State.Packs.AddRange([
            new(rootOwner.Url, "own", "Own", rootOwner.OwnerToken, "owner", HostId: rootOwner.HostId),
            new(remoteOwner.Url, "admin-pack", "Admin pack", remoteOwner.OwnerToken, "owner", CertificatePin: pin, HostId: remoteOwner.HostId),
            new(first.Url, "a", "A", first.OwnerToken, "member-a-id", CertificatePin: pin, HostId: first.HostId),
            new(second.Url, "b", "B", second.OwnerToken, "member-b-id", CertificatePin: pin, HostId: second.HostId)
        ]);
        static string Draft(string profileRoot, string hostId, string packId) => Path.Combine(profileRoot, "Drafts",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(hostId + "\n" + packId))) + ".json");
        Directory.CreateDirectory(Path.Combine(root, "Drafts"));
        var legacyDraft = Draft(root, second.Id, "b"); File.WriteAllText(legacyDraft, "{\"notes\":\"Retained local edits\"}");
        check(HostConnectionMigration.NormalizeDelegated(profile) && profile.State.Hosts.Count == 3
            && profile.State.Hosts.Single(x => x.IsDelegated).HostId == "friends"
            && profile.State.Hosts.Contains(rootOwner) && profile.State.Hosts.Contains(remoteOwner),
            "Legacy delegated pack connections merge by trusted host identity without relabelling real owner credentials");
        check(File.ReadAllText(Draft(root, "friends", "b")) == File.ReadAllText(legacyDraft),
            "Grouping legacy delegated connections retains their original drafts and copies edits to the stable host/pack key");
        var groupIndex = profile.State.Hosts.FindIndex(x => x.IsDelegated);
        profile.State.Hosts[groupIndex] = profile.State.Hosts[groupIndex] with { Name = "Renamed friends host" };
        check(!HostConnectionMigration.NormalizeDelegated(profile)
            && profile.State.Hosts[groupIndex].Name == "Renamed friends host",
            "Opening Share packs again preserves a renamed grouped host and does not repeat the migration");
    }

    private static bool Visible(UIElement element) => element.Visibility == Visibility.Visible;
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
