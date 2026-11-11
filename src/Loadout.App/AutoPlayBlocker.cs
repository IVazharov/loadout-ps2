using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Loadout.App;

/// <summary>
/// While a disc is being backed up, and while it is out for cleaning, Windows is told not to run AutoPlay for discs put in
/// that drive, so putting the disc back doesn't open Explorer or a "what to do" prompt. It registers an
/// IQueryCancelAutoPlay in the Running Object Table, which Windows asks whether or not Loadout is in front; no Windows
/// setting is changed. Disposing it gives AutoPlay back.
/// </summary>
internal sealed class AutoPlayBlocker : IDisposable
{
    // CLSID_QueryCancelAutoPlay: the class moniker Windows looks up in the Running Object Table.
    private static readonly Guid QueryCancelAutoPlay = new("331F1768-05A9-4ddd-B86E-DAE34DDC998A");

    private readonly IRunningObjectTable? _table;
    private readonly int _cookie;

    public AutoPlayBlocker(string drive)
    {
        try
        {
            var clsid = QueryCancelAutoPlay;
            if (NativeMethods.GetRunningObjectTable(0, out var table) != 0 || NativeMethods.CreateClassMoniker(ref clsid, out var moniker) != 0)
            {
                return;
            }

            // ROTFLAGS_REGISTRATIONKEEPSALIVE: the table holds the object until it is revoked.
            _cookie = table.Register(1, new AutoPlayCanceller(drive), moniker);
            _table = table;
        }
        catch (COMException)
        {
            // AutoPlay just isn't held back; the copy itself doesn't need it.
        }
    }

    public void Dispose()
    {
        try
        {
            _table?.Revoke(_cookie);
        }
        catch (COMException)
        {
            // Already gone.
        }
    }

    private static class NativeMethods
    {
        [DllImport("ole32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int GetRunningObjectTable(uint reserved, out IRunningObjectTable table);

        [DllImport("ole32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int CreateClassMoniker(ref Guid clsid, out IMoniker moniker);
    }
}

[ComImport]
[Guid("DDEFE873-6997-4e68-BE26-39B633ADBE12")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IQueryCancelAutoPlay
{
    [PreserveSig]
    int AllowAutoPlay([MarshalAs(UnmanagedType.LPWStr)] string path, uint contentType, [MarshalAs(UnmanagedType.LPWStr)] string label, uint serialNumber);
}

/// <summary>Answers Windows' question for one drive: S_FALSE (no AutoPlay) for it, S_OK for any other.</summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class AutoPlayCanceller(string drive) : IQueryCancelAutoPlay
{
    public int AllowAutoPlay(string path, uint contentType, string label, uint serialNumber) =>
        path.StartsWith(drive, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
}
