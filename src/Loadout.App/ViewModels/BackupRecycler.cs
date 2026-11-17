using System.IO;
using System.Runtime.InteropServices;
using Loadout.Discs;
using Microsoft.VisualBasic.FileIO;

namespace Loadout.App;

/// <summary>
/// Sends a backup's files to the Recycle Bin, so a copy deleted or replaced by mistake can come back. A drive without a
/// Recycle Bin, such as a USB stick or a network share, deletes them for good instead: <see cref="HasRecycleBin"/> says
/// which, so the words can say so.
/// </summary>
internal static class BackupRecycler
{
    /// <summary>Whether Windows keeps files deleted on this folder's drive in a Recycle Bin.</summary>
    public static bool HasRecycleBin(string folder)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folder));
            if (string.IsNullOrEmpty(root))
            {
                return false;
            }

            var info = new NativeMethods.RecycleBinInfo { Size = Marshal.SizeOf<NativeMethods.RecycleBinInfo>() };
            return NativeMethods.SHQueryRecycleBin(root, ref info) == 0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Sends every file of the copy that exists: the image (and a CD's cue) or its chd, its report, hashes, map and map picture.
    /// Throws <see cref="IOException"/> when one is in use or can't be moved there.
    /// </summary>
    public static void Recycle(DiscBackupFiles files)
    {
        ArgumentNullException.ThrowIfNull(files);
        Recycle(files.All);
    }

    /// <summary>Sends each of these files that exists to the Recycle Bin, such as a copy's iso once its chd took its place.</summary>
    public static void Recycle(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var path in paths.Where(File.Exists))
        {
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
    }

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RecycleBinInfo
        {
            public int Size;
            public long Bytes;
            public long Items;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "SHQueryRecycleBinW")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int SHQueryRecycleBin(string rootPath, ref RecycleBinInfo info);
    }
}
