using System.Globalization;
using System.IO;
using Loadout.Persistence;

namespace Loadout.App;

/// <summary>
/// Full exceptions, with stack traces, for failures nothing else handled.
/// The file sits in the portable data folder next to the executable.
/// Above 1 MiB the file is renamed to <c>errors.old.log</c> and a new one starts.
/// </summary>
public static class ErrorLog
{
    public const string FileName = "data/errors.log";

    private const long MaxBytes = 1024 * 1024;

    private static readonly object Lock = new();

    public static string PathOnDisk { get; } =
        Path.Combine(AppContext.BaseDirectory, PortableData.DataFolderName, "errors.log");

    public static void Write(string source, Exception exception)
    {
        var entry = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {source}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PathOnDisk)!);
                var file = new FileInfo(PathOnDisk);
                if (file.Exists && file.Length > MaxBytes)
                {
                    File.Move(PathOnDisk, Path.ChangeExtension(PathOnDisk, ".old.log"), overwrite: true);
                }

                File.AppendAllText(PathOnDisk, entry);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The error file is a convenience. Failing to write it must not add a second failure.
        }
    }
}
