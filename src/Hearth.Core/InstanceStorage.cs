using System.Text.Json;

namespace Hearth.Core;

/// <summary>Copies a complete instance library, verifies it, then switches the saved location.
/// The original remains a backup; cancellation never removes the active installation.</summary>
public static class InstanceStorage
{
    private sealed record MoveJournal(string Source, string Target, string Staging, string Id);
    private sealed record Entry(string RelativePath, long Length, long LastWriteTicks, bool Directory);
    public static string DefaultDirectory(string profileRoot) => Path.Combine(Path.GetFullPath(profileRoot), "Prism", "instances");
    public static string Resolve(string profileRoot, string? selectedDirectory) => string.IsNullOrWhiteSpace(selectedDirectory)
        ? DefaultDirectory(profileRoot) : Path.GetFullPath(selectedDirectory);

    public static async Task MoveAsync(string profileRoot, string source, string target, Action<string> commit,
        Action ensureNotRunning, IProgress<SyncProgress>? progress = null, CancellationToken ct = default)
    {
        profileRoot = Full(profileRoot); source = Full(source); target = Full(target);
        if (Same(source, target)) return;
        if (Contains(source, target) || Contains(target, source)) throw new IOException("Choose a folder outside the current instance folder, not its parent or a subfolder.");
        if (Contains(profileRoot, target) && !Same(target, DefaultDirectory(profileRoot))) throw new IOException("Choose an instance folder outside Hearth's app data folder.");
        if (Same(target, Path.GetPathRoot(target)!)) throw new IOException("Choose a folder on the drive, rather than the drive itself.");
        CheckLinks(source); CheckLinks(target);
        ensureNotRunning(); ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(source))
        {
            if (!Same(source, DefaultDirectory(profileRoot))) throw new IOException("The current instance folder is unavailable. Reconnect its drive before changing the location.");
            Directory.CreateDirectory(source); // A new profile may not have installed its first pack yet.
        }
        Directory.CreateDirectory(profileRoot);
        var journalPath = Path.Combine(profileRoot, "instance-move.json");
        using var moveLock = new FileStream(Path.Combine(profileRoot, "instance-move.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var previous = ReadJournal(journalPath);
        if (previous != null)
        {
            ValidateJournal(previous);
            if (Same(previous.Source, source) && Same(previous.Target, target) && Directory.Exists(target) && !Directory.Exists(previous.Staging))
            {
                // A completed copy can survive a shutdown before the profile was saved.
                await VerifyAsync(source, target, progress, ct);
                ensureNotRunning(); ct.ThrowIfCancellationRequested();
                commit(target); File.Delete(journalPath); return;
            }
            DeleteStaging(previous);
            File.Delete(journalPath); // Any completed destination is retained, never overwritten.
        }
        if (File.Exists(target) || (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()))
            throw new IOException("Choose an empty folder. Hearth won't merge with or overwrite an existing instance library.");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var id = Guid.NewGuid().ToString("N");
        var journal = new MoveJournal(source, target, target + ".hearth-moving-" + id, id);
        File.WriteAllText(journalPath + ".tmp", JsonSerializer.Serialize(journal));
        File.Move(journalPath + ".tmp", journalPath, true);
        var completedCopy = false;
        try
        {
            Directory.CreateDirectory(journal.Staging);
            var entries = Snapshot(source);
            var total = entries.Sum(x => x.Length); long copied = 0;
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                var destination = Path.Combine(journal.Staging, entry.RelativePath);
                if (entry.Directory) { Directory.CreateDirectory(destination); continue; }
                var origin = Path.Combine(source, entry.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                CheckLinks(origin);
                await using (var input = new FileStream(origin, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true))
                await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, true))
                {
                    var buffer = new byte[131072]; int count;
                    while ((count = await input.ReadAsync(buffer, ct)) != 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, count), ct); copied += count;
                        progress?.Report(new("Copying Minecraft instances…", copied, total));
                    }
                    await output.FlushAsync(ct); output.Flush(true);
                }
                File.SetLastWriteTimeUtc(destination, new DateTime(entry.LastWriteTicks, DateTimeKind.Utc));
            }
            await VerifyAsync(source, journal.Staging, progress, ct);
            ensureNotRunning(); ct.ThrowIfCancellationRequested();
            CheckLinks(target);
            if (Directory.Exists(target)) Directory.Delete(target, false); // Only the empty folder the user selected.
            Directory.Move(journal.Staging, target);
            completedCopy = true;
            commit(target);
            File.Delete(journalPath);
        }
        catch
        {
            if (!completedCopy)
            {
                try { DeleteStaging(journal); File.Delete(journalPath); } catch (IOException) { /* The journal makes cleanup retryable. */ }
            }
            throw;
        }
    }

    private static async Task VerifyAsync(string source, string destination, IProgress<SyncProgress>? progress, CancellationToken ct)
    {
        var sourceEntries = Snapshot(source); var targetEntries = Snapshot(destination);
        if (!sourceEntries.Select(x => (x.RelativePath, x.Directory, x.Length)).SequenceEqual(targetEntries.Select(x => (x.RelativePath, x.Directory, x.Length))))
            throw new IOException("The instance files changed during the move. Close Minecraft and Prism, then retry. The original folder is unchanged.");
        var total = sourceEntries.Sum(x => x.Length); long verified = 0;
        foreach (var entry in sourceEntries.Where(x => !x.Directory))
        {
            ct.ThrowIfCancellationRequested();
            var sourcePath = Path.Combine(source, entry.RelativePath); var targetPath = Path.Combine(destination, entry.RelativePath);
            CheckLinks(sourcePath); CheckLinks(targetPath);
            var originalHash = await ModFiles.HashAsync(sourcePath, ct); var copiedHash = await ModFiles.HashAsync(targetPath, ct);
            if (originalHash != copiedHash) throw new IOException("A copied instance file could not be verified. The original folder is unchanged; retry the move.");
            verified += entry.Length; progress?.Report(new("Verifying Minecraft instances…", verified, total));
        }
        if (!sourceEntries.SequenceEqual(Snapshot(source)) || !targetEntries.SequenceEqual(Snapshot(destination)))
            throw new IOException("The instance files changed during the move. Close Minecraft and Prism, then retry.");
    }
    private static List<Entry> Snapshot(string root)
    {
        CheckLinks(root);
        var entries = new List<Entry>();
        if (!Directory.Exists(root)) throw new IOException("The instance folder became unavailable. Reconnect its drive and retry; the previous location is still selected.");
        void Visit(string directory)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("The instance folder contains a linked file or folder. Move it manually before changing the location in Hearth.");
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                entries.Add(new(Path.GetRelativePath(root, path), isDirectory ? 0 : new FileInfo(path).Length,
                    isDirectory ? 0 : File.GetLastWriteTimeUtc(path).Ticks, isDirectory));
                if (isDirectory) Visit(path);
            }
        }
        Visit(root);
        return entries.OrderBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }
    private static MoveJournal? ReadJournal(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<MoveJournal>(File.ReadAllText(path)) ?? throw new IOException("The unfinished instance move record is damaged.") : null;
    private static void ValidateJournal(MoveJournal journal)
    {
        if (!Guid.TryParseExact(journal.Id, "N", out _) || !Same(journal.Staging, journal.Target + ".hearth-moving-" + journal.Id)
            || Contains(journal.Staging, journal.Source) || Contains(journal.Source, journal.Staging))
            throw new IOException("The unfinished instance move record is invalid.");
    }
    private static void DeleteStaging(MoveJournal journal)
    {
        ValidateJournal(journal);
        if (!Directory.Exists(journal.Staging)) return;
        _ = Snapshot(journal.Staging); // Never recursively delete through links.
        Directory.Delete(journal.Staging, true);
    }
    private static void CheckLinks(string path)
    {
        for (var current = Full(path); current != null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Choose a regular folder, not a symbolic link or junction.");
    }
    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool Same(string a, string b) => Full(a).Equals(Full(b), StringComparison.OrdinalIgnoreCase);
    private static bool Contains(string parent, string child) => Same(parent, child) || Full(child).StartsWith(Full(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
