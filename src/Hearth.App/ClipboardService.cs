using System.Runtime.InteropServices;
using System.Windows;

namespace Hearth.App;
public static class ClipboardService
{
    public static Task<bool> SetTextAsync(string text, Action<string> status, string success)
    {
        var data = new DataObject(); data.SetText(text);
        return SetAsync(data, status, success);
    }
    public static async Task<bool> SetAsync(IDataObject data, Action<string> status, string success, Action<IDataObject>? write = null)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try { if (write == null) Clipboard.SetDataObject(data, true); else write(data); status(success); return true; }
            catch (ExternalException) { if (attempt < 3) await Task.Delay(45); }
        }
        status("Windows is using the clipboard. Try Copy again in a moment."); return false;
    }
}
public partial class MainWindow
{
    private void CopyText(string text, string success) => _ = ClipboardService.SetTextAsync(text, Status, success);
    public void ReportRecoverableError(Exception error)
    {
        AppDiagnostics.Log(error);
        Status("That action couldn't finish. Your saved data was kept. Try again. Details: " + AppDiagnostics.LogPath);
    }
}
