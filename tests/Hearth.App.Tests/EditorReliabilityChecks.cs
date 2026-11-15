using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Hearth.App;
using Hearth.Core;

internal static class EditorReliabilityChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public static void Run(string root, Action<bool, string> check, Action<Func<bool>, string> wait)
    {
        var baseApi = new PackFile("mods/fabric-api-old.jar", new string('a', 64), 10);
        var newApi = new PackFile("mods/fabric-api-new.jar", new string('b', 64), 10, "voxy");
        var voxy = new PackFile("mods/voxy.jar", new string('c', 64), 10, "voxy");
        var chosen = ModIdentityValidation.SelectOptional([(baseApi, (IReadOnlyList<string>)["fabric-api"])],
            [(newApi, (IReadOnlyList<string>)["fabric-api"]), (voxy, (IReadOnlyList<string>)["voxy"])]);
        check(chosen.SequenceEqual([voxy]), "Optional terrain keeps Required Fabric API even when the resolver proposes a different filename and version");
        try { ModIdentityValidation.ValidateGroups([(baseApi, (IReadOnlyList<string>)["fabric-api"]), (newApi, (IReadOnlyList<string>)["fabric-api"])]); throw new Exception("Expected duplicate rejection"); }
        catch (InvalidDataException ex) { check(ex.Message.Contains("Keep the Required mods copy") && ex.Message.Contains("Off"), "Shared mod identity validation identifies which dependency copy Off players need"); }
        var first = new PackManifest("pack", "before", "Pack", "1.21.1", "fabric", "0.16.10", [baseApi], "original notes");
        var localChange = baseApi with { Sha256 = new string('d', 64) };
        var remoteChange = baseApi with { Sha256 = new string('e', 64) };
        var anotherMod = new PackFile("mods/another.jar", new string('f', 64), 10);
        var dirty = DraftState.From(new("pack", "Pack", "1.21.1", "fabric", "0.16.10", "before", "invite"), first);
        dirty.Files = [new(localChange, "local.jar")]; dirty.Metadata = dirty.Metadata! with { Name = "My name" };
        var newer = first with { ReleaseId = "newer", Files = [remoteChange, anotherMod], LoaderVersion = "0.16.12", Notes = "latest notes" };
        var rebased = dirty.KeepChangesOn(newer);
        check(rebased.BaseReleaseId == "newer" && rebased.Files.Any(x => x.File == localChange) && rebased.Files.Any(x => x.File == anotherMod)
            && rebased.Metadata!.Name == "My name" && rebased.Metadata.LoaderVersion == "0.16.12" && rebased.Notes == "latest notes",
            "Keeping edits uses the newer baseline, keeps only local edits, and preserves unrelated files, settings and notes");
        check(dirty.ConflictsWith(newer).SequenceEqual([baseApi.Path]), "Stale draft conflict review identifies a file changed by both owners");
        var legacyHost = new HostConnection("legacy", "Legacy", "https://one.example", "owner", IsDelegated: true);
        var unrelated = new JoinedPack("https://two.example", "pack", "Pack", "private-token", "member");
        bool CanUse(HostConnection connection, JoinedPack candidate) => (bool)typeof(MainWindow).GetMethod("CanUsePackCredential", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [connection, candidate])!;
        check(!CanUse(legacyHost, unrelated) && CanUse(legacyHost, unrelated with { HostUrl = legacyHost.Url }),
            "Legacy delegated host credentials are restricted to their own address when no host identity is stored");
        var trusted = legacyHost with { HostId = "host-id", CertificatePin = new string('a', 64) };
        check(CanUse(trusted, unrelated with { HostId = "host-id", CertificatePin = trusted.CertificatePin })
            && !CanUse(trusted, unrelated with { HostUrl = trusted.Url, HostId = "host-id", CertificatePin = new string('b', 64) }),
            "Delegated credentials follow a verified HostId and pin across addresses, and reject another identity at the same address");

        using var fixture = new OwnerFixture();
        var profile = new ProfileStore(root);
        var host = new HostConnection("connection-one", "Owner fixture", fixture.Url, OwnerFixture.OwnerToken, HostId: "stable-host");
        profile.State.Hosts.Add(host); profile.Save();
        var window = new MainWindow(profile);
        try
        {
            Set(window, "selectedHost", host);
            Finish((Task)Invoke(window, "OpenEditor", fixture.Pack)!);
            check(!Get<DraftState>(window, "draft").HasChanges && !FindButton(window, "Review & publish").IsEnabled, "Opening a release creates a clean baseline instead of an unpublished edit");
            var oldRelease = fixture.Pack.ReleaseId; fixture.AdvanceRelease();
            Finish((Task)Invoke(window, "OpenEditor", fixture.Pack)!);
            check(Get<DraftState>(window, "draft").BaseReleaseId == fixture.Pack.ReleaseId && fixture.Pack.ReleaseId != oldRelease
                && !Get<DraftState>(window, "draft").HasChanges, "A clean saved draft silently follows another owner's new release");

            var state = Get<DraftState>(window, "draft"); state.OptionalExpanded = true; state.SelectedGroup = "distanthorizons";
            var path = Path.Combine(root, "custom.jar");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("custom.txt").Open())) writer.Write("custom source");
            Finish((Task)Invoke(window, "AddFiles", new[] { path }, null, "distanthorizons")!);
            check(Descendants(window).OfType<Expander>().Single(x => Equals(x.Header, "Optional terrain mods")).IsExpanded
                && Get<DraftState>(window, "draft").SelectedGroup == "distanthorizons", "Adding a mod preserves the expanded optional section and selected terrain group");
            var name = Descendants(window).OfType<TextBox>().Single(x => AutomationProperties.GetName(x) == "Pack name");
            name.Text = "Renamed pack";
            var metadata = Get<DraftState>(window, "draft").Metadata!;
            check(metadata.Name == "Renamed pack" && FindButton(window, "Review & publish").IsEnabled, "Pack settings edits become publishable draft metadata");
            var stablePath = (string)Invoke(window, "DraftPath")!;
            var newHost = host with { Id = "reconnected-host" };
            profile.State.Hosts.Clear(); profile.State.Hosts.Add(newHost); profile.Save(); Set(window, "selectedHost", newHost);
            Finish((Task)Invoke(window, "OpenEditor", fixture.Pack)!);
            check((string)Invoke(window, "DraftPath")! == stablePath && Get<DraftState>(window, "draft").Metadata!.Name == "Renamed pack",
                "Reconnecting the same verified HostId reuses the saved draft despite a new local connection ID");
            fixture.AdvanceRelease(); Finish((Task)Invoke(window, "OpenEditor", fixture.Pack)!);
            check(Get<DraftState>(window, "draft").HasChanges && FindButton(window, "Discard my changes") is not null
                && FindButton(window, "Keep editing") is not null && !FindButton(window, "Review & publish").IsEnabled,
                "A genuinely edited stale draft offers visible discard and keep-editing actions");
            Finish((Task)Invoke(window, "ResetDraft")!);
            Get<DraftState>(window, "draft").Metadata = new("Published name", "1.21.1", "fabric", "0.16.12");
            Get<DraftState>(window, "draft").Notes = "Players can read these notes";
            Finish((Task)Invoke(window, "PublishDraft")!);
            check(fixture.Pack.Name == "Published name" && fixture.Pack.LoaderVersion == "0.16.12" && fixture.Manifest.Notes == "Players can read these notes",
                "Publishing submits edited pack name, version metadata, and release notes");
            check(!Get<DraftState>(window, "draft").HasChanges && !FindButton(window, "Review & publish").IsEnabled,
                "Successful publication saves a clean baseline including release notes");
            check(profile.State.Packs.Any(x => x.PackId == fixture.Pack.Id), "Publishing adds the owner's pack to the local library");
            Set(window, "selectedPack", fixture.Pack with { ReleaseId = null });
            Set(window, "personalInvitesSupported", true);
            Invoke(window, "RenderMembers", new List<Hearth.Core.MemberInfo>(), true);
            check(Descendants(window).OfType<TextBlock>().Any(x => x.Text.Contains("Publish first"))
                && !Descendants(window).OfType<Button>().Any(x => x.Content?.ToString() is "Reset invite link" or "Create personal invite…"),
                "A pack without a release explains publishing first and does not offer invite actions");
            Set(window, "selectedPack", fixture.Pack);
            var requestTime = DateTimeOffset.Now.AddMinutes(-8);
            Invoke(window, "RenderMembers", new List<Hearth.Core.MemberInfo>
                { new("pending-id", "Alex", "pending", "player", "F1-AB", requestTime, "Alex from school", "personal-id") }, true);
            check(Descendants(window).OfType<TextBlock>().Any(x => x.Text.Contains("invite you made for Alex from school"))
                && Descendants(window).OfType<TextBlock>().Any(x => x.Text == "Requested " + requestTime.LocalDateTime.ToString("g"))
                && FindButton(window, "Decline") is not null && FindButton(window, "Decline all") is not null,
                "Pending rows show personal invite provenance and request time with distinct Decline actions");
            check(FindButton(window, "Reset invite link") is not null
                && Descendants(window).OfType<CheckBox>().Any(x => Equals(x.Content, "Allow the shared invite link")),
                "Published packs expose shared invite reset and the shared-link enable setting");
        }
        finally { window.Close(); }
        void Finish(Task task) { wait(() => task.IsCompleted, "Editor reliability operation completes"); task.GetAwaiter().GetResult(); }
    }
    private static object? Invoke(MainWindow window, string name, params object?[] args) => typeof(MainWindow).GetMethods(Private).Single(x => x.Name == name && x.GetParameters().Length == args.Length).Invoke(window, args);
    private static T Get<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    private static void Set(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    private static Button FindButton(MainWindow window, string label) => Descendants(window).OfType<Button>().Single(x => Equals(x.Content, label));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject item)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(item).OfType<DependencyObject>())
        { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
}
