using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Hearth.App;
using Hearth.Core;

internal static class ModImportChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        var first = Jar(root, "source", "custom.jar", "one");
        var next = Jar(root, "replacement", "custom.jar", "two");
        var another = Jar(root, "source", "another.jar", "three");
        var files = new DataObject(DataFormats.FileDrop, new[] { first, another });
        check(DraftModImport.ReadPaths(files).SequenceEqual(new[] { first, another }), "Explorer file-copy payload preserves multi-file mod selection");
        var text = new DataObject(DataFormats.UnicodeText, $"\"{first}\"\r\n\"{another}\"\r\n\"{first}\"");
        check(DraftModImport.ReadPaths(text).SequenceEqual(new[] { first, another }), "Copy as path accepts quoted JAR paths and removes duplicates");
        check(!DraftModImport.AreJarPaths(["https://example.com/mod.jar"]) && !DraftModImport.AreJarPaths([root])
            && !DraftModImport.AreJarPaths([first, Path.Combine(root, "notes.txt")]), "Mod import rejects links, folders, and mixed non-JAR selections");
        var staged = await DraftModImport.StageAsync([first, another], root, "base", default);
        var snapshot = staged[0].LocalPath!;
        check(snapshot != first && await ModFiles.MatchesAsync(snapshot, staged[0].File), "Import stores a verified independent snapshot of custom mod bytes");
        File.WriteAllText(first, "source changed after importing");
        check(await ModFiles.MatchesAsync(snapshot, staged[0].File), "Changing the original JAR does not break a saved draft");
        var replaced = await DraftModImport.StageAsync([next], root, "base", default);
        var merged = DraftModImport.Apply(staged, replaced);
        check(merged.Count == 2 && merged.Single(x => x.File.Path == "mods/custom.jar").File.Sha256 == replaced[0].File.Sha256
            && staged[0].LocalPath == snapshot, "Same-name replacement updates one entry and preserves the previous draft snapshot");
        var conflicting = Jar(root, "conflict", "custom.jar", "conflict");
        await Reject(() => DraftModImport.StageAsync([next, conflicting], root, "base", default));
        check(staged.Count == 2, "Conflicting same-name files cannot partially mutate the draft");
        await Reject(() => DraftModImport.StageAsync([next, first], root, "base", default));
        check(!Directory.EnumerateFiles(Path.Combine(root, "Drafts"), "*.part", SearchOption.AllDirectories).Any(), "An invalid batch leaves no temporary partial imports");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await DraftModImport.StageAsync([next], root, "base", cancel.Token); throw new Exception("Cancellation was ignored"); }
        catch (OperationCanceledException) { check(true, "Canceled import leaves existing draft files intact"); }
        var blob = new BlobHandler(File.ReadAllBytes(snapshot));
        using var client = new HttpClient(blob);
        using var api = new HearthApi("http://127.0.0.1:17842", "fixture-owner", client);
        var published = new DraftItem(staged[0].File, null);
        var source = await DraftModImport.GetSourceAsync(published, Path.Combine(root, "copy-published"), api, "private-pack", default);
        check(blob.Requests == 1 && blob.OwnerAuthorized && await ModFiles.MatchesAsync(source, published.File), "Copying a published mod uses owner authorization and verifies the downloaded file");
        await DraftModImport.GetSourceAsync(published, Path.Combine(root, "copy-published"), api, "private-pack", default);
        check(blob.Requests == 1, "Copying the same published mod reuses its verified cached bytes");
        blob.Corrupt = true;
        try
        {
            await DraftModImport.GetSourceAsync(published, Path.Combine(root, "copy-corrupt"), api, "private-pack", default);
            throw new Exception("Corrupt copy was accepted");
        }
        catch (IOException) { check(true, "Copying rejects a published mod whose downloaded hash does not match"); }
        check(!Directory.EnumerateFiles(Path.Combine(root, "copy-corrupt"), "*.part", SearchOption.AllDirectories).Any(), "Failed copy removes partial bytes without changing the draft");
    }

    public static void RunUi(string root, Action<bool, string> check, Action<Func<bool>, string> wait)
    {
        using var fixture = new OwnerFixture();
        var store = new ProfileStore(root);
        var host = new HostConnection("import-owner", "Import owner", fixture.Url, OwnerFixture.OwnerToken);
        store.State.Hosts.Add(host);
        var window = new MainWindow(store);
        try
        {
            typeof(MainWindow).GetField("selectedHost", Private)!.SetValue(window, host);
            Finish((Task)Invoke(window, "OpenEditor", fixture.Pack)!);
            check(FindButton(window, "Paste mods") is not null
                && Descendants(window).OfType<Border>().Any(p => p.AllowDrop), "Owner editor exposes paste and a card-wide drop target");
            var path = Jar(root, "clipboard-fixture", "copied-custom.jar", "copied without touching the system clipboard");
            Finish((Task)Invoke(window, "PasteModData", new DataObject(DataFormats.FileDrop, new[] { path }))!);
            check(Get<DraftState>(window, "draft").Files.Any(x => x.File.Path == "mods/copied-custom.jar")
                && fixture.UploadRequests == 0 && fixture.PublishRequests == 0, "Pasting Explorer files uses the draft import pipeline without publishing");
            var search = Descendants(window).OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Search imported mods");
            search.Text = "COPIED-CUSTOM";
            var list = Get<ListBox>(window, "fileList");
            check(list.Items.OfType<ListBoxItem>().Count(x => x.Tag is DraftItem) == 1
                && Get<DraftState>(window, "draft").Files.Count == 3, "Pack search filters case-insensitively without changing the draft");
            list.SelectedItem = list.Items.OfType<ListBoxItem>().Single(x => x.Tag is DraftItem);
            check(FindButton(window, "Remove").IsEnabled, "Selecting a filtered mod enables selection actions");
            search.Text = "does-not-exist";
            check(list.Items.OfType<ListBoxItem>().All(x => x.Tag is not DraftItem)
                && !FindButton(window, "Remove").IsEnabled, "No matching mods shows an empty result and disables selection actions");
            search.Text = "";
            Finish((Task)Invoke(window, "PasteModData", new DataObject(DataFormats.UnicodeText, "https://example.com/mod.jar"))!);
            check(Get<DraftState>(window, "draft").Files.Count == 3, "Invalid clipboard contents leave the owner draft unchanged");
            Program.Capture(window, "mod-editor");
            Program.Capture(window, "mod-editor-min", 1000, 720);
            check(!Descendants(window).OfType<Button>().Any(x => Equals(x.Content, "Search mods")),
                "Searching imported mods stays in the local list without an online catalog");
        }
        finally { window.Close(); }
        void Finish(Task task) { wait(() => task.IsCompleted, "Mod import UI operation completes"); task.GetAwaiter().GetResult(); }
    }

    private static async Task Reject(Func<Task> work)
    {
        try { await work(); throw new Exception("Expected invalid JAR batch to be rejected"); }
        catch (InvalidDataException) { }
    }
    private sealed class BlobHandler(byte[] bytes) : HttpMessageHandler
    {
        public int Requests;
        public bool OwnerAuthorized, Corrupt;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            OwnerAuthorized = request.Headers.Authorization?.ToString() == "Bearer fixture-owner"
                && request.RequestUri!.AbsolutePath.StartsWith("/packs/private-pack/blobs/", StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Corrupt ? new byte[bytes.Length] : bytes) });
        }
    }
    private static string Jar(string root, string dir, string filename, string content)
    {
        var folder = Path.Combine(root, dir); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, filename);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry("custom.txt").Open()); writer.Write(content);
        return path;
    }
    private static object? Invoke(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
    private static T Get<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static Button FindButton(MainWindow window, string name) => Descendants(window).OfType<Button>().Single(x => Equals(x.Content, name));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
