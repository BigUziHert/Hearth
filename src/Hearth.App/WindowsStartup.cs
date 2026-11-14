using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Hearth.App;

/// <summary>Per-user sign-in startup. Reading settings never changes Windows configuration.</summary>
public static class WindowsStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled(string profileRoot)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        var command = key?.GetValue(GetValueName(profileRoot), null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        return command != null && command.Equals(BuildCommand(profileRoot), StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(string profileRoot, bool enabled)
    {
        var name = GetValueName(profileRoot);
        if (!enabled)
        {
            using var existing = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            existing?.DeleteValue(name, throwOnMissingValue: false);
            return;
        }

        var command = BuildCommand(profileRoot);
        // Windows documents a 260-character limit for Run command lines.
        if (command.Length > 260)
            throw new InvalidOperationException("Hearth's installation or profile path is too long for Windows startup. Install Hearth in its default location and try again.");
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key.SetValue(name, command, RegistryValueKind.String);
    }

    public static string GetValueName(string profileRoot)
    {
        var root = NormalizeRoot(profileRoot);
        var defaultRoot = NormalizeRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hearth"));
        if (root.Equals(defaultRoot, StringComparison.OrdinalIgnoreCase)) return "Hearth";
        return "Hearth-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())))[..20];
    }

    public static string BuildCommand(string profileRoot, string? executablePath = null, string? managedAssemblyPath = null)
    {
        if (executablePath == null)
        {
            var assembly = typeof(WindowsStartup).Assembly.Location;
            var appHost = Path.ChangeExtension(assembly, ".exe");
            if (File.Exists(appHost)) executablePath = appHost;
            else if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                executablePath = Environment.ProcessPath;
                managedAssemblyPath = assembly;
            }
            else throw new InvalidOperationException("The Hearth executable could not be found. Reinstall Hearth before enabling Windows startup.");
        }
        var command = QuoteArgument(Path.GetFullPath(executablePath!));
        if (managedAssemblyPath != null) command += " " + QuoteArgument(Path.GetFullPath(managedAssemblyPath));
        return command + " --startup --profile " + QuoteArgument(NormalizeRoot(profileRoot));
    }

    private static string NormalizeRoot(string root) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    private static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { backslashes++; continue; }
            result.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes);
            result.Append(character);
            backslashes = 0;
        }
        return result.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
