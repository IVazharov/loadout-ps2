using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Loadout.App;

/// <summary>Opens Explorer with files picked, as its own Show in folder does: the files of one folder in one window.</summary>
internal static class ShellFolder
{
    /// <summary>Opens one Explorer window for each folder the files are in, with those files picked. Files that are gone are left out.</summary>
    public static void ShowFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var folders = paths
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .GroupBy(path => Path.GetDirectoryName(path)!, StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            if (!Show(folder.Key, [.. folder]))
            {
                // Explorer's command line picks one file.
                Process.Start("explorer.exe", "/select,\"" + folder.First() + "\"")?.Dispose();
            }
        }
    }

    private static bool Show(string folder, IReadOnlyList<string> files)
    {
        var folderItem = IntPtr.Zero;
        var items = new List<IntPtr>();
        try
        {
            if (NativeMethods.SHParseDisplayName(folder, IntPtr.Zero, out folderItem, 0, out _) != 0)
            {
                return false;
            }

            foreach (var file in files)
            {
                if (NativeMethods.SHParseDisplayName(file, IntPtr.Zero, out var item, 0, out _) == 0)
                {
                    items.Add(item);
                }
            }

            return items.Count > 0 && NativeMethods.SHOpenFolderAndSelectItems(folderItem, (uint)items.Count, [.. items], 0) == 0;
        }
        finally
        {
            foreach (var item in items)
            {
                Marshal.FreeCoTaskMem(item);
            }

            if (folderItem != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(folderItem);
            }
        }
    }

    private static class NativeMethods
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr item, uint attributesIn, out uint attributesOut);

        [DllImport("shell32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, IntPtr[] items, uint flags);
    }
}
