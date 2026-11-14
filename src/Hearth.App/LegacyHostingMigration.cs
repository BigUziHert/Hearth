using System.IO;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace Hearth.App;

public static class LegacyHostingMigration
{
    public static bool ServiceInstalled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\HearthHost");
        return key != null;
    }

    public static void Adopt(ProfileStore profile)
    {
        if (profile.State.LegacyHostingMigrated) return;
        if (ServiceInstalled()) return;
        var normalRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hearth");
        if (!Path.GetFullPath(profile.Root).TrimEnd(Path.DirectorySeparatorChar).Equals(normalRoot, StringComparison.OrdinalIgnoreCase)) return;
        var descriptor = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Hearth", "retired-host.json");
        if (!File.Exists(descriptor)) return;
        AdoptDescriptor(profile, descriptor, System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? "");
    }

    // The installer records the original state before retiring the service.
    // Adopt() waits for its registration to be gone before using this descriptor.
    public static void AdoptDescriptor(ProfileStore profile, string descriptor, string ownerSid)
    {
        if (profile.State.LegacyHostingMigrated) return;
        string contents;
        try { contents = File.ReadAllText(descriptor); }
        catch (UnauthorizedAccessException)
        {
            // The descriptor is intentionally readable only by the original
            // host owner. A different Windows user can still use the launcher.
            return;
        }
        using var document = JsonDocument.Parse(contents);
        var data = document.RootElement;
        if (data.GetProperty("formatVersion").GetInt32() != 1 || data.GetProperty("ownerSid").GetString() != ownerSid) return;
        var directory = data.GetProperty("dataDirectory").GetString() ?? "";
        var port = data.GetProperty("port").GetInt32();
        if (!Path.IsPathFullyQualified(directory) || port is < 1024 or > 65535 || !File.Exists(Path.Combine(directory, "host.json")))
            throw new IOException("The previous host data could not be found. Re-run the Hearth installer to finish migrating hosting.");
        var local = profile.State.Hosts.FirstOrDefault(host => Uri.TryCreate(host.Url, UriKind.Absolute, out var uri) && uri.IsLoopback && uri.Port == port);
        profile.State.LocalHostDataDirectory = Path.GetFullPath(directory);
        profile.State.LocalHostingPort = port;
        profile.State.LocalHostOwnerToken = local?.OwnerToken ?? "";
        profile.State.LocalHostConnectionId = local?.Id ?? "";
        // Without the original key, never generate a replacement or auto-start.
        profile.State.LocalHostingEnabled = local != null && (data.GetProperty("wasRunning").GetBoolean()
            || (data.TryGetProperty("wasEnabled", out var enabled) && enabled.GetBoolean()));
        profile.State.LegacyHostingMigrated = true;
        profile.Save();
    }
}
