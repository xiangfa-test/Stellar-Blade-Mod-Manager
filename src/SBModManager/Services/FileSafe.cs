using System.Text;

namespace SBModManager.Services;

/// <summary>
/// Crash-safe text writes. A plain <see cref="File.WriteAllText(string, string)"/> truncates the
/// target before writing, so a process kill (or a full disk) mid-write leaves a half-finished
/// JSON file behind. Every state file here is parsed strictly, and a truncated one silently
/// degrades to "no config" / "no profiles" / "mod metadata unreadable" — the user then sees
/// settings reset or mods vanish. Writing a sibling temp file and renaming it over the target
/// makes each save atomic, so readers only ever see the previous or the new content.
/// </summary>
public static class FileSafe
{
    /// <summary>UTF-8 without BOM, matching what File.WriteAllText used before.</summary>
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private const int MaxAttempts = 5;

    /// <summary>
    /// Write <paramref name="contents"/> to <paramref name="path"/> atomically.
    /// Throws when the file cannot be replaced, so callers keep their existing error handling.
    /// </summary>
    public static void WriteAllText(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new StreamWriter(stream, Utf8NoBom, 1024, leaveOpen: true))
                {
                    writer.Write(contents);
                    writer.Flush();
                }
                // Flush before the rename so a power loss cannot reorder data behind the directory entry.
                stream.Flush(flushToDisk: true);
            }

            Commit(temp, path);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>Same contract as <see cref="File.WriteAllLines(string, IEnumerable{string})"/>.</summary>
    public static void WriteAllLines(string path, IEnumerable<string> lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            builder.Append(line).Append(Environment.NewLine);
        }
        WriteAllText(path, builder.ToString());
    }

    /// <summary>Move the finished temp file onto the target, retrying around AV / search indexers.</summary>
    private static void Commit(string temp, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(target))
                {
                    File.Move(temp, target);
                    return;
                }

                try
                {
                    // Atomic on NTFS: either the old or the new file stays in place.
                    File.Replace(temp, target, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    // File.Replace refuses some targets (network shares, odd filters): fall back to a move.
                    File.Move(temp, target, overwrite: true);
                }
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < MaxAttempts)
            {
                Thread.Sleep(40 * attempt);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A leftover temp file is harmless; CleanupStale removes it on the next start.
        }
    }

    /// <summary>
    /// Remove temp files left behind when a previous run was killed mid-save.
    /// Only touches its own <c>*.tmp-????????</c> pattern and only files older than an hour.
    /// </summary>
    public static int CleanupStale(string root, TimeSpan? olderThan = null)
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(root)) return 0;
            var cutoff = DateTime.UtcNow - (olderThan ?? TimeSpan.FromHours(1));
            foreach (var file in Directory.EnumerateFiles(root, "*.tmp-*", SearchOption.AllDirectories))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) > cutoff) continue;
                    File.Delete(file);
                    removed++;
                }
                catch
                {
                    // locked or already gone: leave it for next time
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"清理临时写入文件失败: {ex.Message}");
        }
        return removed;
    }
}
