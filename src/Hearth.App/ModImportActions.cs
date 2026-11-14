using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Hearth.Core;

namespace Hearth.App;

public partial class MainWindow
{
    private string modFilter = "";

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
        return AddFiles(paths, groupOverride: "base");
    }

    private async Task CopySelectedMods() => await Run("Preparing mods to copy…", async ct =>
    {
        var selected = SelectedDraftItems();
        if (selected.Count == 0) return;
        var host = selectedHost!;
        using var api = new HearthApi(host.Url, host.OwnerToken, certificatePin: host.CertificatePin);
        var paths = new StringCollection();
        foreach (var item in selected)
            paths.Add(await DraftModImport.GetSourceAsync(item, store.Root, api, selectedPack!.Id, ct));
        ct.ThrowIfCancellationRequested();
        Clipboard.SetFileDropList(paths);
        Status($"Copied {selected.Count} mod file(s). Paste into another pack or a folder in File Explorer.");
    });

    private void SaveDraftFiles(List<DraftItem> files)
    {
        var before = draft!.Files;
        draft.Files = files;
        try { SaveDraft(); }
        catch { draft.Files = before; throw; }
    }
}
