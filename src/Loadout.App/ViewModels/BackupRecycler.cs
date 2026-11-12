using System.IO;
using Loadout.Discs;
using Microsoft.VisualBasic.FileIO;

namespace Loadout.App;

/// <summary>Sends a backup's files to the Recycle Bin, so a copy deleted or replaced by mistake can come back.</summary>
internal static class BackupRecycler
{
    /// <summary>
    /// Sends every file of the copy that exists: the image (and a CD's cue), its report, hashes, map and map picture.
    /// Throws <see cref="IOException"/> when one is in use or can't be moved there.
    /// </summary>
    public static void Recycle(DiscBackupFiles files)
    {
        ArgumentNullException.ThrowIfNull(files);
        foreach (var path in files.All.Where(File.Exists))
        {
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
    }
}
