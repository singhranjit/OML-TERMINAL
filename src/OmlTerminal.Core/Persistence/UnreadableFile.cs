namespace OmlTerminal.Core.Persistence;

/// <summary>
/// When a data file can't be read (a crash mid-write, a sync conflict, a bad hand edit) the app starts with empty
/// data - and the next save would overwrite the damaged file for good. Keeping a copy first means nothing is lost:
/// the damaged file can still be repaired by hand or restored.
/// </summary>
public static class UnreadableFile
{
    /// <summary>Copies <paramref name="path"/> to "&lt;name&gt;.unreadable-&lt;timestamp&gt;" beside it. Never throws.</summary>
    /// <returns>The copy's path, or null if there was nothing to keep or the copy failed.</returns>
    public static string? Keep(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length == 0) return null;
            // Already kept (the app was simply started again with the same damaged file): don't pile up copies.
            var bytes = File.ReadAllBytes(path);
            var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
            foreach (var old in Directory.EnumerateFiles(dir, Path.GetFileName(path) + ".unreadable-*"))
                if (new FileInfo(old).Length == bytes.Length && File.ReadAllBytes(old).AsSpan().SequenceEqual(bytes)) return old;
            var copy = $"{path}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(path, copy, overwrite: false);
            return copy;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
