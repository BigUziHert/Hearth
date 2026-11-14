using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Hearth.App;
using Hearth.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class OwnerFlowChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static void Run(string root, Action<bool, string> check, Action<Func<bool>, string> wait)
    {
        Directory.CreateDirectory(root);
        using var fixture = new OwnerFixture();
        var profile = new ProfileStore(root);
        var host = new HostConnection("owner-fixture", "Owner fixture", fixture.Url, OwnerFixture.OwnerToken);
        profile.State.Hosts.Add(host);
        var pending = new JoinedPack(host.Url, fixture.Pack.Id, fixture.Pack.Name, "pending-credential", "pending-member", TerrainMode.Voxy);
        profile.State.Packs.Add(pending); profile.Save();
        var synchronizer = new PackSynchronizer(root);
        var originalInstance = synchronizer.GetInstanceDirectory(host.Url, fixture.Pack.Id);
        var personalFile = Path.Combine(originalInstance, ".minecraft", "options.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(personalFile)!); File.WriteAllText(personalFile, "key_key.forward:87");
        var window = new MainWindow(profile);
        void Finish(Task task, string message) { wait(() => task.IsCompleted, message); task.GetAwaiter().GetResult(); }
        try
        {
            Set(window, "selectedHost", host);
            Set(window, "selectedPack", fixture.Pack);
            Invoke(window, "OpenOwnedPack");
            var joined = new ProfileStore(root).State.Packs.Single();
            check(joined.Token == OwnerFixture.OwnerToken && joined.MemberId == "owner", "Opening an owned pack replaces an older pending player credential with owner access");
            check(joined.Terrain == TerrainMode.Voxy && joined.HostUrl == pending.HostUrl && joined.PackId == pending.PackId
                && File.ReadAllText(personalFile) == "key_key.forward:87", "Opening an owned pack preserves its terrain preference, instance identity, and personal files");

            Finish((Task)Invoke(window, "OpenEditor", fixture.Pack)!, "Owner editor loads the published release");
            var firstJar = Jar(root, "custom-one", "custom.jar", "custom build one");
            var secondJar = Jar(root, "custom-two", "custom.jar", "custom build two");
            Finish((Task)Invoke(window, "AddFiles", new[] { firstJar }, null, "base")!, "Adding a custom JAR finishes");
            var draft = Get<DraftState>(window, "draft");
            var originalCustom = draft.Files.Single(item => item.File.Path == "mods/custom.jar");
            check(draft.BaseReleaseId == "release-one" && originalCustom.LocalPath != firstJar && File.ReadAllBytes(originalCustom.LocalPath!).SequenceEqual(File.ReadAllBytes(firstJar)), "Adding a JAR snapshots its bytes in a draft based on the current release");
            Finish((Task)Invoke(window, "AddFiles", new[] { secondJar }, originalCustom, null)!, "Replacing a custom JAR finishes");
            draft = Get<DraftState>(window, "draft");
            var replacementPath = draft.Files.Single(item => item.File.Path == "mods/custom.jar").LocalPath;
            check(draft.Files.Count(item => item.File.Path == "mods/custom.jar") == 1
                && draft.Files.Single(item => item.File.Path == "mods/custom.jar").File.Sha256 != originalCustom.File.Sha256,
                "Replacing a custom JAR leaves exactly one new version in the draft");
            Invoke(window, "ShowOwnerPackList");
            Finish((Task)Invoke(window, "OpenEditor", fixture.Pack)!, "Owner can leave and reopen a saved draft");
            check(Get<DraftState>(window, "draft").Files.Any(item => item.LocalPath == replacementPath), "Leaving the editor and reopening restores the unpublished replacement");

            var list = Get<ListBox>(window, "fileList");
            list.SelectedItem = list.Items.OfType<ListBoxItem>().Single(item => item.Tag is DraftItem draftItem && draftItem.File.Path == "mods/custom.jar");
            FindButton(window, "Remove").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(Get<DraftState>(window, "draft").Files.All(item => item.File.Path != "mods/custom.jar")
                && fixture.UploadRequests == 0 && fixture.PublishRequests == 0,
                "Remove only changes the saved draft; editing does not upload or publish");
            Finish((Task)Invoke(window, "AddFiles", new[] { secondJar }, null, "base")!, "Staging the replacement for publication finishes");

            fixture.AdvanceRelease();
            Finish((Task)Invoke(window, "PublishDraft")!, "Stale publish preflight finishes without uploading");
            check(fixture.UploadRequests == 0 && fixture.PublishRequests == 0, "A stale draft is detected before uploading or attempting a publish");
            check(Get<DraftState>(window, "draft").Files.Any(item => item.LocalPath == replacementPath)
                && !FindButton(window, "Review & publish").IsEnabled
                && Descendants(window).OfType<TextBlock>().Any(text => text.Text.Contains("Your draft has been kept")),
                "A newer release preserves unpublished edits and shows an actionable stale-draft state");
            Finish((Task)Invoke(window, "ResetDraft")!, "Discard reloads the current release");
            check(Get<DraftState>(window, "draft").BaseReleaseId == fixture.Pack.ReleaseId
                && Get<DraftState>(window, "draft").Files.All(item => item.LocalPath == null)
                && FindButton(window, "Review & publish").IsEnabled,
                "Discard replaces the draft with the latest complete release and enables publishing again");

            fixture.ConflictOnNextPublish = true;
            Finish((Task)Invoke(window, "PublishDraft")!, "Concurrent publication conflict refreshes the editor");
            check(fixture.PublishRequests == 1 && !FindButton(window, "Review & publish").IsEnabled
                && Get<PackManifest>(window, "published").ReleaseId == fixture.Pack.ReleaseId,
                "A publish race refreshes the latest release and prevents repeated blind retries");
            Finish((Task)Invoke(window, "ResetDraft")!, "Concurrent release can be reloaded");
            Finish((Task)Invoke(window, "AddFiles", new[] { secondJar }, null, "base")!, "Owner can stage a JAR after refreshing the release");
            Finish((Task)Invoke(window, "PublishDraft")!, "A refreshed draft publishes successfully");
            check(fixture.PublishRequests == 2 && fixture.UploadRequests == 1
                && Get<DraftState>(window, "draft").BaseReleaseId == fixture.Pack.ReleaseId
                && Get<DraftState>(window, "draft").Files.Any(item => item.File.Path == "mods/custom.jar")
                && Get<DraftState>(window, "draft").Files.All(item => item.LocalPath == null),
                "Publishing after refresh uploads the staged JAR and saves the complete release as the new draft base");

            fixture.IsAdministrator = false;
            Invoke(window, "ShowOwnerPackList");
            wait(() => FindButton(window, "New pack").Visibility == Visibility.Collapsed, "Delegated owners do not see host-only New pack creation");
        }
        finally { window.Close(); }
    }

    private static object? Invoke(MainWindow window, string name, params object?[] arguments) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, arguments);
    private static T Get<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Set(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    private static Button FindButton(MainWindow window, string label) => Descendants(window).OfType<Button>().Single(button => Equals(button.Content, label));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static string Jar(string root, string directory, string name, string content)
    {
        var folder = Path.Combine(root, directory); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry("custom.txt").Open()); writer.Write(content);
        return path;
    }
}

internal sealed class OwnerFixture : IDisposable
{
    public const string OwnerToken = "fixture-owner-credential-that-never-leaves-this-pc";
    private readonly WebApplication app;
    public string Url { get; }
    public PackSummary Pack { get; private set; } = new("owner-pack", "Owner pack", "1.21.1", "fabric", "0.16.10", "release-one", "fixture-invite");
    public PackManifest Manifest { get; private set; } = new("owner-pack", "release-one", "Owner pack", "1.21.1", "fabric", "0.16.10", [new("mods/required.jar", new string('a', 64), 42), new("mods/voxy.jar", new string('b', 64), 42, "voxy")]);
    public int UploadRequests;
    public int PublishRequests;
    public bool ConflictOnNextPublish;
    public bool IsAdministrator = true;
    public OwnerFixture()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        app = builder.Build();
        app.Run(Respond);
        Task.Run(() => app.StartAsync()).GetAwaiter().GetResult();
        Url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    }
    public void AdvanceRelease()
    {
        var release = Guid.NewGuid().ToString("N");
        Pack = Pack with { ReleaseId = release }; Manifest = Manifest with { ReleaseId = release };
    }
    private async Task Respond(HttpContext context)
    {
        var request = context.Request; object result;
        var status = 200;
        var path = request.Path.Value ?? "";
        if (path == "/owner/packs") result = new[] { Pack };
        else if (path == "/owner/status") { status = IsAdministrator ? 200 : 403; result = new { error = "Only the host owner can view status." }; }
        else if (path.EndsWith("/access")) result = new AccessResponse(request.Headers.Authorization == "Bearer " + OwnerToken ? "approved" : "pending", "owner", Pack.Name);
        else if (path.EndsWith("/manifest")) result = Manifest;
        else if (path.Contains("/blobs/"))
        { Interlocked.Increment(ref UploadRequests); await request.Body.CopyToAsync(Stream.Null); result = new { uploaded = true }; }
        else if (path.EndsWith("/releases"))
        {
            Interlocked.Increment(ref PublishRequests);
            if (ConflictOnNextPublish)
            {
                ConflictOnNextPublish = false; AdvanceRelease(); status = 409; result = new { error = "Someone published a newer release." };
            }
            else
            {
                var publish = await JsonSerializer.DeserializeAsync<PublishRequest>(request.Body, JsonDefaults.Options);
                AdvanceRelease(); Manifest = Manifest with { Files = publish!.Files, Notes = publish.Notes }; result = Manifest;
            }
        }
        else { status = 404; result = new { error = "Unknown fixture route" }; }
        context.Response.StatusCode = status; context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(result, JsonDefaults.Options));
    }
    public void Dispose() => Task.Run(async () => { await app.StopAsync(); await app.DisposeAsync(); }).GetAwaiter().GetResult();
}
