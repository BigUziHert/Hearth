using System.Collections;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Hearth.Core;
public static class ProcessCommandLines
{
    public static async Task<List<(string Name, string? Command)>> ReadAsync(CancellationToken ct = default)
    {
        if (!OperatingSystem.IsWindows()) return [];
        try { return await Task.Run(Query, ct).WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
        catch (TimeoutException ex) { throw new IOException("Windows couldn't check running Minecraft instances. Close Minecraft and Prism, then try again.", ex); }
    }
    [SupportedOSPlatform("windows")]
    private static List<(string, string?)> Query()
    {
        object? locator = null, services = null, rows = null;
        try
        {
            locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator", true)!);
            services = ((dynamic)locator!).ConnectServer(".", @"root\cimv2");
            // .NET's COM IEnumerable adapter requests a fresh enumerator. The
            // WMI forward-only flag (32) makes that operation fail with
            // DISP_E_EXCEPTION; return-immediately (16) retains a usable set.
            rows = ((dynamic)services).ExecQuery("SELECT Name,CommandLine FROM Win32_Process WHERE Name='prismlauncher.exe' OR Name='java.exe' OR Name='javaw.exe'", "WQL", 16);
            var result = new List<(string, string?)>();
            foreach (var row in (IEnumerable)rows)
            {
                try { result.Add(((string)((dynamic)row).Name, (string?)((dynamic)row).CommandLine)); }
                finally { Release(row); }
            }
            return result;
        }
        catch (COMException ex) { throw new IOException("Windows couldn't check running Minecraft instances. Try again.", ex); }
        finally { Release(rows); Release(services); Release(locator); }
    }
    [SupportedOSPlatform("windows")]
    private static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}
