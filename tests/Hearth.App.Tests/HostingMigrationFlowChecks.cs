using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using Hearth.App;

internal static class HostingMigrationFlowChecks
{
    public static void Run(string root, Action<bool, string> check)
    {
        var profile = new ProfileStore(root);
        var descriptor = Path.Combine(root, "unreadable-retired-host.json");
        File.WriteAllText(descriptor, "{}");
        var file = new FileInfo(descriptor);
        var original = file.GetAccessControl();
        var denied = file.GetAccessControl();
        denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny));
        try
        {
            file.SetAccessControl(denied);
            var inaccessible = false;
            try { File.ReadAllText(descriptor); }
            catch (UnauthorizedAccessException) { inaccessible = true; }
            check(inaccessible, "Migration fixture reproduces an unreadable descriptor from another Windows account");
            LegacyHostingMigration.AdoptDescriptor(profile, descriptor, WindowsIdentity.GetCurrent().User!.Value);
            check(!profile.State.LegacyHostingMigrated && !profile.State.LocalHostingEnabled && profile.State.Hosts.Count == 0,
                "An inaccessible legacy descriptor does not prevent using Hearth or create a host");
        }
        finally { file.SetAccessControl(original); }

        profile.State.LegacyHostingMigrated = true;
        LegacyHostingMigration.AdoptDescriptor(profile, descriptor, WindowsIdentity.GetCurrent().User!.Value);
        check(!profile.State.LocalHostingEnabled, "A completed migration does not reread a stale descriptor");
    }
}
