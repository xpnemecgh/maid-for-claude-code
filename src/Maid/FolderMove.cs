using System.IO;

namespace Maid;

/// <summary>
/// Moves a project folder with everything in it: a rename on the same drive, otherwise a copy that the
/// original is deleted after (only once the copy is complete; a failed copy is taken back). Links
/// (symbolic links, junctions) are moved as links, not as what they point to. Also checks the names
/// a folder can be renamed to.
/// </summary>
internal static class FolderMove
{
    private const int ErrorNotSameDevice = unchecked((int)0x80070011);

    /// <summary>
    /// Moves <paramref name="source"/> to <paramref name="target"/>, which must not exist or be an empty
    /// folder. Returns a warning when the copy is complete but the original could not be deleted in full.
    /// <paramref name="progress"/> is told the number of files copied so far (only a copy reports it).
    /// Throws what the file system throws; the source is then left as it was.
    /// </summary>
    public static string? Move(string source, string target, Action<int>? progress = null)
    {
        bool emptyTarget = Directory.Exists(target);
        if (emptyTarget)
        {
            if (Directory.EnumerateFileSystemEntries(target).Any()) throw new IOException($"{target} already exists and is not empty.");
            Directory.Delete(target);
        }
        else if (File.Exists(target))
        {
            throw new IOException($"{target} is a file.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        try
        {
            if (SameRoot(source, target))
            {
                try
                {
                    Directory.Move(source, target);
                    return null;
                }
                catch (IOException e) when (e.HResult == ErrorNotSameDevice)
                {
                    // Another volume mounted under the same drive letter: copy after all.
                }
            }

            try
            {
                CopyTree(source, target, progress);
            }
            catch
            {
                DeleteTree(target);
                throw;
            }
        }
        catch when (emptyTarget)
        {
            try { Directory.CreateDirectory(target); } catch (Exception) { }
            throw;
        }

        var (left, firstError) = DeleteTree(source);
        return left == 0 ? null
            : $"The copy is complete, but {left} item{(left == 1 ? "" : "s")} of the old folder could not be deleted ({firstError}); {source} is still there.";
    }

    private static bool SameRoot(string a, string b) =>
        string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);

    private static readonly string[] ReservedNames =
        ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).SelectMany(n => new[] { $"COM{n}", $"LPT{n}" })];

    /// <summary>What is wrong with <paramref name="name"/> as a folder's new name; null when Windows takes it as it is.</summary>
    public static string? CheckName(string name)
    {
        if (name.Length == 0) return "Enter the folder's new name.";
        if (name.IndexOfAny(['\\', '/']) >= 0) return "Enter a name, not a path: the folder stays where it is (Move folder… moves it).";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "A folder name cannot contain any of < > : \" | ? *";
        if (name is "." or "..") return $"\"{name}\" cannot be a folder's name.";
        // Windows drops them without a word, so the folder would not end up with the name asked for.
        if (name.EndsWith('.') || name.EndsWith(' ')) return "A folder name cannot end with a dot or a space.";
        if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(name).TrimEnd(' '), StringComparer.OrdinalIgnoreCase))
            return $"\"{name}\" is reserved by Windows.";
        if (name.Length > 255) return "That name is too long.";
        return null;
    }

    internal static void CopyTree(string source, string target, Action<int>? progress)
    {
        int files = 0;
        var directories = new List<(DirectoryInfo From, string To)>();
        var pending = new Stack<(DirectoryInfo From, string To)>();
        pending.Push((new DirectoryInfo(source), target));
        while (pending.Count > 0)
        {
            var (from, to) = pending.Pop();
            Directory.CreateDirectory(to);
            directories.Add((from, to));
            foreach (var item in from.EnumerateFileSystemInfos())
            {
                var destination = Path.Combine(to, item.Name);
                if (item.LinkTarget is { } link)
                {
                    if (item is DirectoryInfo) Directory.CreateSymbolicLink(destination, link);
                    else File.CreateSymbolicLink(destination, link);
                }
                else if (item is DirectoryInfo directory)
                {
                    pending.Push((directory, destination));
                }
                else
                {
                    // Keeps the attributes (read-only, hidden) and the time it was last written.
                    File.Copy(item.FullName, destination);
                    progress?.Invoke(++files);
                }
            }
        }
        // Last, as copying into a folder touches its times.
        foreach (var (from, to) in directories)
        {
            var copy = new DirectoryInfo(to);
            copy.Attributes = from.Attributes & ~FileAttributes.ReparsePoint;
            copy.CreationTimeUtc = from.CreationTimeUtc;
            copy.LastWriteTimeUtc = from.LastWriteTimeUtc;
        }
    }

    /// <summary>Deletes a folder tree, read-only files included (git keeps its objects read-only); links go, not what they point to.</summary>
    internal static (int Left, string? FirstError) DeleteTree(string path)
    {
        int left = 0;
        string? firstError = null;
        void Failed(Exception e)
        {
            left++;
            firstError ??= e.Message;
        }

        var root = new DirectoryInfo(path);
        if (!root.Exists) return (0, null);
        var order = new List<DirectoryInfo>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            order.Add(directory);
            IEnumerable<FileSystemInfo> items;
            try { items = directory.EnumerateFileSystemInfos().ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Failed(e); continue; }
            foreach (var item in items)
            {
                try
                {
                    if (item is DirectoryInfo child && child.LinkTarget is null)
                    {
                        pending.Push(child);
                        continue;
                    }
                    if (item.Attributes.HasFlag(FileAttributes.ReadOnly))
                        item.Attributes &= ~FileAttributes.ReadOnly;
                    item.Delete(); // a link to a folder is deleted as the link alone
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Failed(e); }
            }
        }
        for (int i = order.Count - 1; i >= 0; i--)
        {
            try
            {
                if (order[i].Attributes.HasFlag(FileAttributes.ReadOnly))
                    order[i].Attributes &= ~FileAttributes.ReadOnly;
                order[i].Delete();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A folder that still holds what could not be deleted is not counted again.
                if (Directory.Exists(order[i].FullName) && !Directory.EnumerateFileSystemEntries(order[i].FullName).Any()) Failed(e);
            }
        }
        return (left, firstError);
    }

    /// <summary>
    /// Moves Claude Code's transcripts (and memory) of a project along with it, so that resuming and
    /// the conversation history carry on in the new folder. Best effort.
    /// </summary>
    public static void MoveTranscripts(string oldPath, string newPath, string? claudeConfigDirectory = null)
    {
        try
        {
            var from = ConversationHistory.GetTranscriptDirectory(oldPath, claudeConfigDirectory);
            var to = ConversationHistory.GetTranscriptDirectory(newPath, claudeConfigDirectory);
            if (!Directory.Exists(from) || string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;
            if (!Directory.Exists(to))
            {
                Directory.Move(from, to);
                return;
            }
            foreach (var item in new DirectoryInfo(from).EnumerateFileSystemInfos().ToList())
            {
                var destination = Path.Combine(to, item.Name);
                if (File.Exists(destination) || Directory.Exists(destination)) continue;
                if (item is DirectoryInfo directory) directory.MoveTo(destination);
                else ((FileInfo)item).MoveTo(destination);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The project moved; only resuming an older conversation there will not find it.
        }
    }
}
