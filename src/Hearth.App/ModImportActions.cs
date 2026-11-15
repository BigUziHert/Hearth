using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Text.Json;
using Hearth.Core;

namespace Hearth.App;

public partial class MainWindow
{
    private string modFilter = "";
    private const string ModClipboardFormat = "Hearth.ModFiles.v1";
    private sealed record ClipboardModFile(string Path, string Group);

    private void AttachModImport(FrameworkElement editor)
    {
        editor.AllowDrop = true;
        editor.Focusable = true;
        editor.MouseLeftButtonDown += (_, e) => { if (ReferenceEquals(e.OriginalSource, editor)) editor.Focus(); };
        editor.PreviewDragOver += (_, e) =>
        {
            e.Effects = !busy && DraftModImport.AreJarPaths(DraftModImport.ReadPaths(e.Data)) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        editor.PreviewDrop += (_, e) =>
        {
            e.Handled = true;
            if (!busy) _ = PasteModData(e.Data);
        };
        editor.PreviewKeyDown += (_, e) =>
        {
            // Text fields retain their normal copy/paste behavior.
            if (Keyboard.Modifiers != ModifierKeys.Control || Keyboard.FocusedElement is TextBoxBase || busy) return;
            if (e.Key == Key.V) { e.Handled = true; PasteMods(); }
            else if (e.Key == Key.C && fileList?.IsKeyboardFocusWithin == true) { e.Handled = true; _ = CopySelectedMods(); }
        };
    }

    private void PasteMods()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data is null) { Status("Copy .jar files in File Explorer, then choose Paste mods."); return; }
            _ = PasteModData(data);
        }
        catch (System.Runtime.InteropServices.ExternalException) { Status("Windows couldn't read the clipboard. Try Paste mods again."); }
    }

    private Task PasteModData(IDataObject data)
    {
        var paths = DraftModImport.ReadPaths(data);
        if (!DraftModImport.AreJarPaths(paths))
        {
            Status("Copy .jar files in File Explorer, or copy their full paths. Folders and web links cannot be added as mods.");
            return Task.CompletedTask;
        }
        if (data.GetDataPresent(ModClipboardFormat) && data.GetData(ModClipboardFormat) is string metadata)
        {
            try
            {
                var entries = JsonSerializer.Deserialize<List<ClipboardModFile>>(metadata) ?? [];
                if (entries.Count < paths.Length || entries.Any(x => x.Group is not ("base" or "voxy" or "distanthorizons")
                    || !paths.Contains(x.Path, StringComparer.OrdinalIgnoreCase)) || entries.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length)
                    throw new JsonException();
                return Run("Pasting mods…", async ct =>
                {
                    var staged = new List<DraftItem>();
                    foreach (var group in entries.GroupBy(x => x.Group))
                        staged.AddRange(await DraftModImport.StageAsync(group.Select(x => x.Path), store.Root, group.Key, ct));
                    ct.ThrowIfCancellationRequested();
                    SaveDraftFiles(DraftModImport.Apply(draft!.Files, staged)); RenderEditor();
                    Status("Mods pasted with their Required, Voxy, and Distant Horizons groups preserved. Review before publishing.");
                });
            }
            catch (JsonException) { Status("The copied mod information is damaged. Copy the mods again from Hearth."); return Task.CompletedTask; }
        }
        return AddFiles(paths, groupOverride: "base");
    }

    private async Task CopySelectedMods() => await Run("Preparing mods to copy…", async ct =>
    {
        var selected = SelectedDraftItems();
        if (selected.Count == 0) return;
        var host = selectedHost!;
        using var api = CreateOwnerApi(host);
        var paths = new StringCollection();
        var entries = new List<ClipboardModFile>();
        foreach (var item in selected)
        {
            var path = await DraftModImport.GetSourceAsync(item, store.Root, api, selectedPack!.Id, ct);
            if (!paths.Contains(path)) paths.Add(path); entries.Add(new(path, item.File.Group));
        }
        ct.ThrowIfCancellationRequested();
        var data = new DataObject(); data.SetFileDropList(paths); data.SetData(ModClipboardFormat, JsonSerializer.Serialize(entries));
        await ClipboardService.SetAsync(data, Status, $"Copied {selected.Count} mod file(s). Paste into another pack or a folder in File Explorer.");
    });

    private void SaveDraftFiles(List<DraftItem> files)
    {
        var before = draft!.Files;
        draft.Files = files;
        try { SaveDraft(); }
        catch { draft.Files = before; throw; }
    }
}
