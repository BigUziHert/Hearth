using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using Hearth.Core;

namespace Hearth.App;
public partial class MainWindow : Window
{
    private readonly ProfileStore store;
    private readonly PackSynchronizer sync;
    private readonly PrismService prism;
    private CancellationTokenSource? operation;
    private string activePage = "library";
    private string pendingInvite = "";
    private bool inviteQueued;
    private HostConnection? selectedHost;
    private PackSummary? selectedPack;
    private PackManifest? published;
    private DraftState? draft;
    private ListBox? fileList;
    private TextBlock? draftSummary;
    private string selectedGroup = "base";
    private bool busy;
    private bool exitRequested;
    private TaskCompletionSource? operationFinished;
    public MainWindow(ProfileStore profile)
    {
        InitializeComponent(); store = profile; sync = new(store.Root); prism = new(store.Root);
        ProfileLabel.Text = store.State.DisplayName.ToUpperInvariant(); VersionLabel.Text = "HEARTH  " + AppInfo.Version;
        BrandIcon.Content = PackArtwork.Create(0, 30);
        LibraryNav.Content = IconLabel("My modpacks", "M2,2 L8,2 8,8 2,8 Z M12,2 L18,2 18,8 12,8 Z M2,12 L8,12 8,18 2,18 Z M12,12 L18,12 18,18 12,18 Z");
        AddPackNav.Content = IconLabel("Add modpack", "M10,2 L10,18 M2,10 L18,10");
        PublishNav.Content = IconLabel("Manage modpacks", "M2,6 L10,2 18,6 10,10 Z M2,10 L10,14 18,10 M2,14 L10,18 18,14");
        HostsNav.Content = IconLabel("Hosting", "M2,2 L18,2 18,8 2,8 Z M2,12 L18,12 18,18 2,18 Z M5,5 L6,5 M5,15 L6,15 M12,5 L15,5 M12,15 L15,15");
        SettingsNav.Content = IconLabel("Settings", "M2,4 L18,4 M2,10 L18,10 M2,16 L18,16 M6,1 L6,7 M14,7 L14,13 M7,13 L7,19");
        PackOptionsButton.Content = new System.Windows.Shapes.Path { Data = Geometry.Parse("M1,4 A1.5,1.5 0 1 0 4,4 A1.5,1.5 0 1 0 1,4 M8,4 A1.5,1.5 0 1 0 11,4 A1.5,1.5 0 1 0 8,4 M15,4 A1.5,1.5 0 1 0 18,4 A1.5,1.5 0 1 0 15,4"), Fill = Color("#CED8CE"), VerticalAlignment = VerticalAlignment.Center };
        SourceInitialized += (_, _) => { var dark = 1; _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int)); };
        try { HeroImage.Source = new BitmapImage(new Uri("pack://application:,,,/Hearth.App;component/Assets/pack-landscape.png")); } catch (IOException) { }
        TerrainOff.Checked += Terrain_Checked; TerrainVoxy.Checked += Terrain_Checked; TerrainDh.Checked += Terrain_Checked;
        ShowLibrary();
        Closing += (_, e) =>
        {
            var app = Application.Current as App;
            if (busy && app?.IsExiting != true && !(store.State.CloseToTray && app?.TrayAvailable == true))
            {
                e.Cancel = true;
                Dispatcher.BeginInvoke(new Action(RequestQuit));
            }
        };
        Closed += (_, _) => { packStatusCancellation?.Cancel(); packStatusCancellation?.Dispose(); ownerListRequest?.Cancel(); ownerListRequest?.Dispose(); hostingStatusTimer?.Stop(); };
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    private static FrameworkElement IconLabel(string label, string geometry)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse(geometry), Stroke = Color("#C2CDC4"), StrokeThickness = 1.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Stretch = Stretch.Uniform, Width = 18, Height = 18, Margin = new(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center }); return row;
    }
    private static Brush Color(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;
    private static TextBlock Text(string value, double size = 14, bool bold = false, string color = "#F0F4F0") => new() { Text = value, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = Color(color), Margin = new(0, 0, 0, 9), TextWrapping = TextWrapping.Wrap };
    private static Button Button(string label, Action action, bool secondary = false)
    {
        var button = new Button { Content = label, Background = Color(secondary ? "#2B3334" : "#62C629"), Foreground = Color("#FFFFFF"), BorderBrush = Color(secondary ? "#566160" : "#78DA3D"), BorderThickness = new(1) };
        button.Click += (_, _) => action(); return button;
    }
    private static WrapPanel Row(params UIElement[] children) { var row = new WrapPanel { Margin = new(0, 8, 0, 4), VerticalAlignment = VerticalAlignment.Center }; foreach (var child in children) row.Children.Add(child); return row; }
    private StackPanel Card(string title, string? subtitle = null, string background = "#22292B")
    {
        var content = new StackPanel(); if (title.Length > 0) content.Children.Add(Text(title, 19, true)); if (subtitle != null) content.Children.Add(Text(subtitle, 13, false, "#ADBAB1"));
        Page.Children.Add(new Border { Background = Color(background), BorderBrush = Color("#3D4846"), BorderThickness = new(1), CornerRadius = new(10), Padding = new(23), Margin = new(0, 0, 0, 18), Child = content }); return content;
    }
    private static TextBox Field(Panel panel, string label, string value = "", string? hint = null)
    {
        panel.Children.Add(Text(label, 12, true)); var field = new TextBox { Text = value }; System.Windows.Automation.AutomationProperties.SetName(field, label); panel.Children.Add(field); if (hint != null) panel.Children.Add(Text(hint, 12, false, "#ADBAB1")); return field;
    }
    private static ComboBox Select(Panel panel, string label, IEnumerable<string> items, string selected)
    {
        panel.Children.Add(Text(label, 12, true)); var select = new ComboBox { ItemsSource = items, SelectedItem = selected }; System.Windows.Automation.AutomationProperties.SetName(select, label); panel.Children.Add(select); return select;
    }
    private void PageStart(string key, string title, string eyebrow)
    {
        hostingStatusTimer?.Stop(); hostingStatusTimer = null;
        packStatusCancellation?.Cancel(); activePage = key; PageTitle.Text = title; PageEyebrow.Text = eyebrow; Page.Children.Clear(); PageActions.Children.Clear();
        LibraryView.Visibility = key == "library" ? Visibility.Visible : Visibility.Collapsed;
        PageHeader.Visibility = PageScroller.Visibility = key == "library" ? Visibility.Collapsed : Visibility.Visible;
        LibraryNav.Background = Color(key == "library" ? "#30383A" : "#262D2F");
        PublishNav.Background = Color(key == "publish" ? "#3C542D" : "#1B2022");
        HostsNav.Background = Color(key == "hosts" ? "#3C542D" : "#1B2022");
        AddPackNav.Background = Color(key == "join" ? "#3C542D" : "#252B2D");
        SettingsNav.Background = Color(key == "settings" ? "#30383A" : "#1B2022");
        RefreshSidebar();
        PageScroller.ScrollToTop();
    }
    private void Status(string text) { StatusText.Text = text; StatusText.ToolTip = text; }
    private async Task Run(string label, Func<CancellationToken, Task> work)
    {
        if (busy || exitRequested) return; busy = true; operation = new(); operationFinished = new(TaskCreationOptions.RunContinuationsAsynchronously); MainContent.IsEnabled = SidebarPanel.IsEnabled = false;
        Progress.Visibility = CancelButton.Visibility = Visibility.Visible; Status(label);
        try { await work(operation.Token); }
        catch (OperationCanceledException) { if (!exitRequested) Status("Canceled. You can try again."); }
        catch (Exception ex)
        {
            if (!exitRequested)
            {
                Status(ex.Message);
                MessageBox.Show(this, ex.Message, "Hearth", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        finally
        {
            busy = false; operation.Dispose(); operation = null; MainContent.IsEnabled = SidebarPanel.IsEnabled = true;
            Progress.Visibility = CancelButton.Visibility = Visibility.Collapsed;
            operationFinished.TrySetResult(); operationFinished = null;
            if (inviteQueued && !exitRequested) { inviteQueued = false; ShowAddPack(); }
        }
    }
    public async void RequestQuit()
    {
        if (exitRequested) return;
        exitRequested = true;
        Status("Closing Hearth…");
        var completion = operationFinished?.Task;
        packStatusCancellation?.Cancel();
        operation?.Cancel();
        if (completion != null) await completion;
        if (Application.Current is App app) app.ExitApplication(); else Close();
    }
    private void Library_Click(object sender, RoutedEventArgs e) => ShowLibrary();
    private void AddPack_Click(object sender, RoutedEventArgs e) => ShowAddPack();
    private void Publish_Click(object sender, RoutedEventArgs e) => ShowPublishing();
    private void Hosts_Click(object sender, RoutedEventArgs e) => ShowHosts();
    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();
    private void Cancel_Click(object sender, RoutedEventArgs e) => operation?.Cancel();
    private void SetupPrism_Click(object sender, RoutedEventArgs e) => _ = InstallPrism();
    public void OpenInvite(string value) { pendingInvite = value; if (!busy) ShowAddPack(); else { inviteQueued = true; Status("Your invite will open when the current operation finishes."); } }
    private string? AvailablePrismExe() => File.Exists(store.State.PrismExecutable) ? store.State.PrismExecutable : prism.FindExecutable();
    private string PrismExe() => AvailablePrismExe() ?? throw new InvalidOperationException("Set up Prism using the button below the pack or in Settings.");
    private async Task Play(JoinedPack original) => await Run("Checking this pack's latest release…", async ct =>
    {
        packStatusCancellation?.Cancel(); var exe = PrismExe(); var pack = store.State.Packs.First(p => p.PackId == original.PackId && p.HostUrl == original.HostUrl);
        using var api = new HearthApi(pack.HostUrl, pack.Token);
        try
        {
            var result = await sync.SyncAsync(api, pack, new Progress<SyncProgress>(p => { Status(p.Message); if (activePage == "library" && IsSelected(pack)) SetPackStatus(p.Message, "#D6BE70"); }), ct);
            ct.ThrowIfCancellationRequested(); prism.Launch(exe, result.InstanceId); Status("Pack ready. Continue in Prism to start Minecraft."); ShowLibrary();
        }
        catch (HostApiException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound)
        {
            ct.ThrowIfCancellationRequested();
            await RefreshSelectedStatus(); Status("Pack access or its release has changed. Follow the next step shown above.");
        }
        catch { if (!ct.IsCancellationRequested && !exitRequested) _ = RefreshSelectedStatus(); throw; }
    });
    private async Task InstallPrism() => await Run("Downloading Prism from its official release…", async ct =>
    {
        store.State.PrismExecutable = await prism.InstallAsync(new Progress<SyncProgress>(p => Status(p.Message)), ct); store.Save();
        ct.ThrowIfCancellationRequested(); prism.Open(store.State.PrismExecutable);
        if (activePage == "library") ShowLibrary();
        else if (activePage == "settings") ShowSettings();
        Status("In Prism, finish setup and sign in through Settings → Accounts → Add Microsoft. Then close Prism, return here, and press Play.");
    });
    private void OpenFolder(string path) { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { path } }); }
}
