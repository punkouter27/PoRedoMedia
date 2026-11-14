using System.Security.Cryptography;
using System.Text;

namespace PoRedoMedia.Movie.Library;

/// <summary>The few things every part of the app does with files: make room for one, name one by its content, and work in scratch space.</summary>
public static class Files
{
    /// <summary>
    /// Everything the app leaves in the temp folder lives under this one folder, so it can be cleared in one go.
    /// In the temp folder and never an episode folder: that is usually inside a synced Documents folder, where the
    /// sync client locks new files.
    /// </summary>
    public static string ScratchRoot { get; } = Path.Combine(Path.GetTempPath(), "PoRedoMedia", "movie");

    /// <summary>
    /// Links the app shows end up on a button the user clicks. Anything other than a web address (file shares,
    /// search-ms:, other protocol handlers) could make that click do something on the machine, so it is refused.
    /// </summary>
    public static bool IsWebLink(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    /// <summary>Makes sure the folder a file is about to be written into exists.</summary>
    public static void EnsureFolderFor(string filePath) => Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);

    public static string TextHash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static string ContentHash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>
    /// Puts <paramref name="source"/> where <paramref name="target"/> is, even while the old target is being played.
    /// Windows refuses to move a file over one that is open ("Access to the path is denied"), but lets an open file
    /// shared for deleting (as the movie endpoints serve them) be renamed and deleted. So the old one steps aside
    /// first, and is deleted once the new one is in place; if it is still held open, it goes when the reader lets go.
    /// </summary>
    public static void ReplaceInUse(string source, string target)
    {
        if (!File.Exists(target))
        {
            File.Move(source, target);
            return;
        }
        var aside = $"{target}.{Guid.NewGuid():N}.old";
        File.Move(target, aside);
        try
        {
            File.Move(source, target);
        }
        catch
        {
            File.Move(aside, target); // put the old one back, so a failed swap leaves things as they were
            throw;
        }
        try
        {
            File.Delete(aside);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Still held open somewhere that did not share delete; it is only a stale copy.
        }
    }

    /// <summary>A new empty folder for one job's working files. The job deletes it when it ends; <see cref="SweepScratch"/> catches the rest.</summary>
    public static string NewScratchFolder(string job) =>
        Directory.CreateDirectory(Path.Combine(ScratchRoot, $"{job}-{Guid.NewGuid():N}")).FullName;

    /// <summary>A path for one scratch file, not yet created.</summary>
    public static string NewScratchFile(string extension)
    {
        Directory.CreateDirectory(ScratchRoot);
        return Path.Combine(ScratchRoot, $"{Guid.NewGuid():N}{extension}");
    }

    /// <summary>
    /// Removes working files a crash or a locked file left behind: anything directly under <see cref="ScratchRoot"/>
    /// untouched for <paramref name="olderThan"/>. Folders named in <paramref name="keep"/> look after themselves.
    /// </summary>
    public static void SweepScratch(TimeSpan olderThan, params string[] keep)
    {
        var root = new DirectoryInfo(ScratchRoot);
        if (!root.Exists) return;
        foreach (var entry in root.EnumerateFileSystemInfos().Where(e => DateTime.UtcNow - e.LastWriteTimeUtc > olderThan && !keep.Contains(e.Name, StringComparer.OrdinalIgnoreCase)))
        {
            try
            {
                if (entry is DirectoryInfo folder) folder.Delete(recursive: true);
                else entry.Delete();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Still in use by another copy of the app: it is caught next time.
            }
        }
    }
}
