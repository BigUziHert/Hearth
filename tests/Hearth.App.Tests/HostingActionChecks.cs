using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Hearth.App;
using Hearth.Core;

internal static class HostingActionChecks
{
    public static void Run(string root, Action<bool, string> check)
    {
        var profile = new ProfileStore(root);
        profile.State.LocalHostConnectionId = "local-actions-fixture";
        profile.State.Hosts.Add(new("local-actions-fixture", "This PC", "http://127.0.0.1:17842", "fixture-owner-key"));
        var window = new MainWindow(profile);
        try
        {
            typeof(MainWindow).GetMethod("ShowHosts", BindingFlags.Instance | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null)!.Invoke(window, null);
            var buttons = Descendants(window).OfType<Button>().ToList();
            var enable = buttons.Single(x => Equals(x.Content, "Enable hosting"));
            var retry = buttons.Single(x => Equals(x.Content, "Retry connection setup"));
            var stop = buttons.Single(x => Equals(x.Content, "Disable hosting"));
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
