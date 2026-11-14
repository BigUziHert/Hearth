using System.IO.Compression;
using System.Text.Json;
using Hearth.Core;

internal static class InstalledPlayChecks
{
    public static async Task RunAsync(string root, Action<bool, string> check)
    {
        const string host = "http://127.0.0.1:17842";
        const string packId = "offline-verification";
        var sync = new PackSynchronizer(root);
        var instance = sync.GetInstanceDirectory(host, packId);
        var managed = Path.Combine(instance, ".minecraft", "mods", "fixture.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(managed)!);
        using (var zip = ZipFile.Open(managed, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("fixture.txt").Open())) writer.Write("Complete mod bytes");
        var original = await File.ReadAllBytesAsync(managed);
        var file = await ModFiles.InspectAsync(managed);
        var manifest = new PackManifest(packId, "release-one", "Offline fixture", "1.21.1", "fabric", "0.16.10", [file]);
        var installed = new InstalledPack(host, packId, manifest.ReleaseId, TerrainMode.Off, [file], manifest);
        var installedPath = Path.Combine(instance, ".hearth", "installed.json");
        Directory.CreateDirectory(Path.GetDirectoryName(installedPath)!);
        await File.WriteAllTextAsync(installedPath, JsonSerializer.Serialize(installed, JsonDefaults.Options));
        var metadata = Path.Combine(instance, "mmc-pack.json");
        var generated = PackSynchronizer.CreatePrismMetadata(manifest);
        await File.WriteAllBytesAsync(metadata, generated);
        var personalConfig = Path.Combine(instance, "instance.cfg");
        await File.WriteAllTextAsync(personalConfig, "[General]\r\nInstanceType=OneSix\r\nMaxMemAlloc=6144\r\n");
        var world = Path.Combine(instance, ".minecraft", "saves", "My world", "level.dat");
        Directory.CreateDirectory(Path.GetDirectoryName(world)!);
        await File.WriteAllTextAsync(world, "personal world bytes");
        var settings = await File.ReadAllBytesAsync(personalConfig);

        var verified = await sync.VerifyInstalledAsync(host, packId);
        check(verified.ReleaseId == installed.ReleaseId, "Offline play verifies its complete installed release without a host request");
        await File.WriteAllTextAsync(metadata, """
            {"formatVersion":1,"components":[
              {"uid":"net.minecraft","version":"1.21.1","cachedName":"Minecraft","cachedVersion":"1.21.1"},
              {"uid":"net.fabricmc.fabric-loader","version":"0.16.10","cachedName":"Fabric Loader"},
              {"uid":"org.lwjgl3","version":"3.3.3","dependencyOnly":true}],"cachedMetadata":true}
            """);
        await sync.VerifyInstalledAsync(host, packId);
        check(true, "Offline verification accepts Prism's cached fields and automatic components");

        File.Delete(managed);
        await NeedsRepair(() => sync.VerifyInstalledAsync(host, packId), check, "A missing managed JAR blocks offline launch with a repair action");
        var damaged = original.ToArray(); damaged[^1] ^= 1;
        await File.WriteAllBytesAsync(managed, damaged);
        await NeedsRepair(() => sync.VerifyInstalledAsync(host, packId), check, "A changed same-length JAR is detected by its hash before offline launch");
        await File.WriteAllBytesAsync(managed, original);
        File.Delete(metadata);
        await NeedsRepair(() => sync.VerifyInstalledAsync(host, packId), check, "Missing Prism version metadata blocks offline launch");
        await File.WriteAllTextAsync(metadata, "{ damaged");
        await NeedsRepair(() => sync.VerifyInstalledAsync(host, packId), check, "Malformed Prism metadata reports a repair action");
        await File.WriteAllTextAsync(metadata, "{\"formatVersion\":1,\"components\":[{\"uid\":\"net.minecraft\",\"version\":\"1.21.1\"}]}");
        await NeedsRepair(() => sync.VerifyInstalledAsync(host, packId), check, "Missing mod loader cannot turn an installed modpack into a vanilla launch");
        await File.WriteAllBytesAsync(metadata, generated);

        await File.WriteAllTextAsync(installedPath, JsonSerializer.Serialize(installed with { PackId = "different-pack" }, JsonDefaults.Options));
        await NeedsRepair(() => sync.VerifyInstalledAsync(host, packId), check, "A mismatched installation record asks for backup recovery rather than promising automatic repair", recordRecovery: true);
        await File.WriteAllTextAsync(installedPath, "{ damaged");
        await NeedsRepair(() => sync.VerifyInstalledAsync(host, packId), check, "A malformed installation record keeps files safe and gives the correct recovery action", recordRecovery: true);
        await NeedsRepair(() => Task.Run(() => sync.GetInstalled(host, packId)), check,
            "Normal Play also reports the recovery action when reading a damaged installation record", recordRecovery: true);
        File.Delete(installedPath);
        await NeedsRepair(() => sync.VerifyInstalledAsync(host, packId), check, "A missing ownership record does not guess which mods may be replaced", recordRecovery: true);
        await File.WriteAllTextAsync(installedPath, JsonSerializer.Serialize(installed, JsonDefaults.Options));
        var transaction = Path.Combine(instance, ".hearth", "transaction");
        Directory.CreateDirectory(Path.Combine(transaction, "backup"));
        await File.WriteAllBytesAsync(Path.Combine(transaction, "backup", "0"), original);
        await File.WriteAllTextAsync(Path.Combine(transaction, "journal.json"), "{\"phase\":\"prepared\",\"entries\":[{\"path\":\".minecraft/mods/fixture.jar\",\"existed\":true,\"backup\":\"0\"}]}");
        await File.WriteAllBytesAsync(managed, damaged);
        await sync.VerifyInstalledAsync(host, packId);
        check((await File.ReadAllBytesAsync(managed)).SequenceEqual(original) && !Directory.Exists(transaction),
            "Offline play recovers an interrupted update before validating the previous complete release");
        check((await File.ReadAllBytesAsync(personalConfig)).SequenceEqual(settings) && await File.ReadAllTextAsync(world) == "personal world bytes",
            "Offline validation and recovery preserve personal Prism settings and world bytes");

        check(PrismService.IsInstanceProcess("javaw.exe", "javaw.exe --gameDir \"" + Path.Combine(instance, ".minecraft").Replace('\\', '/') + "\"", instance),
            "A running Minecraft with forward-slash paths blocks updates to its instance");
        var prismRoot = Path.Combine(root, "Prism");
        check(PrismService.IsInstanceProcess("prismlauncher.exe", "prismlauncher.exe --dir \"" + prismRoot.Replace('\\', '/') + "\"", instance),
            "A Prism window with forward-slash paths is recognized as this profile's launcher");
        check(!PrismService.IsInstanceProcess("java.exe", "java.exe --gameDir \"" + instance + "-different/.minecraft\"", instance)
            && !PrismService.IsInstanceProcess("prismlauncher.exe", "prismlauncher.exe --dir \"" + prismRoot + "-different\"", instance)
            && !PrismService.IsInstanceProcess("prismlauncher.exe", "prismlauncher.exe --dir \"" + prismRoot + " different\"", instance),
            "Unrelated instances and profiles with a shared path prefix do not block Play");
        check(!PrismService.IsInstanceProcess("java.exe", "java.exe -jar minecraft-server.jar nogui", instance)
            && !PrismService.IsInstanceProcess("java.exe", null, instance),
            "Unrelated or protected Minecraft server processes do not block player updates");
    }

    private static async Task NeedsRepair(Func<Task> action, Action<bool, string> check, string message, bool recordRecovery = false)
    {
        try { await action(); }
        catch (IOException exception) when (exception.Message.Contains(recordRecovery ? "Restore .hearth/installed.json from a backup" : "Connect to its host and press Play", StringComparison.Ordinal))
        {
            check(true, message); return;
        }
        throw new InvalidOperationException(message);
    }
}
