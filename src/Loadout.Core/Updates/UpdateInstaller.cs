using System.IO.Compression;
using System.Net.Http.Headers;

namespace Loadout.Updates;

/// <summary>A release's files unpacked and checked, waiting to replace this Loadout's.</summary>
public sealed record PreparedUpdate(AppVersion Version, string Folder, IReadOnlyList<string> Files);

/// <summary>Why an update stopped, in words for the user. Nothing of Loadout's has changed when it is thrown.</summary>
public sealed class UpdateInstallException : Exception
{
    public UpdateInstallException()
    {
    }

    public UpdateInstallException(string message)
        : base(message)
    {
    }

    public UpdateInstallException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Updates Loadout in its own folder: download the release's zip into the work folder, unpack and check it
/// (<see cref="IExecutableCheck"/>), then swap the files. Each replaced file is kept with <c>.old</c> added until the
/// new Loadout has started and calls <see cref="CleanUp"/>; if a swap fails part way, the files already swapped are
/// put back. Windows lets a running program's file be renamed, which is what lets Loadout replace itself.
/// </summary>
public sealed class UpdateInstaller
{
    public const string ExecutableName = "LoadoutPS2.exe";
    public const string BackupSuffix = ".old";

    // A release zip is a few MB; this is far above any real one.
    private const long MaxUnpackedBytes = 512L * 1024 * 1024;
    private const string ReplacedList = "replaced.txt";

    private readonly HttpClient _http;
    private readonly string _clientVersion;
    private readonly IExecutableCheck _check;

    public UpdateInstaller(HttpClient http, string clientVersion, string appFolder, string workFolder, IExecutableCheck check)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        ArgumentException.ThrowIfNullOrWhiteSpace(clientVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(appFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(workFolder);
        _clientVersion = clientVersion;
        AppFolder = Path.GetFullPath(appFolder);
        WorkFolder = Path.GetFullPath(workFolder);
        _check = check ?? throw new ArgumentNullException(nameof(check));
    }

    public string AppFolder { get; }

    public string WorkFolder { get; }

    private string DownloadFolder => Path.Combine(WorkFolder, "download");

    private string StagingFolder => Path.Combine(WorkFolder, "staging");

    /// <summary>Whether Loadout may create and replace files in its folder (not so under Program Files, for one).</summary>
    public bool CanWriteAppFolder()
    {
        var probe = Path.Combine(AppFolder, "update-write-test-" + Environment.ProcessId + ".tmp");
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Downloads the offered zip; <paramref name="progress"/> gets the bytes so far.</summary>
    public async Task<string> DownloadAsync(UpdateOffer offer, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);
        Directory.CreateDirectory(DownloadFolder);
        var target = Path.Combine(DownloadFolder, Path.GetFileName(offer.Download.Name));
        var part = target + ".part";
        using var request = new HttpRequestMessage(HttpMethod.Get, offer.Download.DownloadUrl);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("LoadoutPS2", _clientVersion));
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateInstallException("GitHub answered the download with an error (" + (int)response.StatusCode + "). Try again later.");
            }

            long done = 0;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    done += read;
                    if (offer.Download.Size > 0 && done > offer.Download.Size)
                    {
                        throw new UpdateInstallException("The download is larger than GitHub said it would be.");
                    }

                    progress?.Report(done);
                }
            }

            if (offer.Download.Size > 0 && done != offer.Download.Size)
            {
                throw new UpdateInstallException("The download ended early. Try again.");
            }

            File.Move(part, target, overwrite: true);
            return target;
        }
        catch (HttpRequestException ex)
        {
            throw new UpdateInstallException("The download from GitHub broke off. Check that this PC is online and try again.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateInstallException("The download from GitHub stopped answering. Try again.", ex);
        }
        catch (IOException ex)
        {
            throw new UpdateInstallException("The download could not be saved in " + WorkFolder + ": " + ex.Message, ex);
        }
        finally
        {
            TryDelete(part);
        }
    }

    /// <summary>Unpacks the zip into the work folder and checks that its Loadout is signed and of the version offered.</summary>
    public PreparedUpdate Prepare(string zip, AppVersion version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zip);
        ArgumentNullException.ThrowIfNull(version);
        var staging = StagingFolder;
        try
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            Directory.CreateDirectory(staging);
            var root = staging + Path.DirectorySeparatorChar;
            var files = new List<string>();
            long total = 0;
            using (var archive = ZipFile.OpenRead(zip))
            {
                foreach (var entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                    {
                        continue;
                    }

                    var destination = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                    if (Path.IsPathRooted(entry.FullName) || !destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(destination).EndsWith(BackupSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new UpdateInstallException("The download holds a file Loadout won't unpack: " + entry.FullName);
                    }

                    total += entry.Length;
                    if (total > MaxUnpackedBytes)
                    {
                        throw new UpdateInstallException("The download unpacks to far more than a Loadout release.");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    entry.ExtractToFile(destination, overwrite: true);
                    files.Add(Path.GetRelativePath(staging, destination));
                }
            }

            var executable = Path.Combine(staging, ExecutableName);
            if (!File.Exists(executable))
            {
                throw new UpdateInstallException("The download has no " + ExecutableName + ".");
            }

            if (_check.Problem(executable, version) is { } problem)
            {
                throw new UpdateInstallException(problem + " Nothing was changed.");
            }

            return new PreparedUpdate(version, staging, files);
        }
        catch (InvalidDataException ex)
        {
            throw new UpdateInstallException("The download is not a readable zip. Try again.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new UpdateInstallException("The download could not be unpacked in " + WorkFolder + ": " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Puts the prepared files in place of this Loadout's, keeping each replaced one with <c>.old</c> added.
    /// On any failure the swapped files are put back and nothing has changed.
    /// </summary>
    public void Apply(PreparedUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var done = new List<(string Target, string? Backup)>();
        try
        {
            // The executable last: everything else is in place before the file that matters most changes.
            foreach (var relative in update.Files.OrderBy(file => string.Equals(file, ExecutableName, StringComparison.OrdinalIgnoreCase)))
            {
                var source = Path.Combine(update.Folder, relative);
                var target = Path.Combine(AppFolder, relative);
                string? backup = null;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    backup = target + BackupSuffix;
                    if (File.Exists(backup))
                    {
                        File.Delete(backup);
                    }

                    File.Move(target, backup);
                }

                done.Add((target, backup));
                File.Move(source, target);
            }

            Directory.CreateDirectory(WorkFolder);
            File.WriteAllLines(
                Path.Combine(WorkFolder, ReplacedList),
                done.Where(step => step.Backup is not null).Select(step => step.Backup!));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var (target, backup) in Enumerable.Reverse(done))
            {
                try
                {
                    if (backup is null)
                    {
                        TryDelete(target);
                    }
                    else
                    {
                        if (File.Exists(target))
                        {
                            File.Delete(target);
                        }

                        File.Move(backup, target);
                    }
                }
                catch (Exception undo) when (undo is IOException or UnauthorizedAccessException)
                {
                    // Keep putting back the rest; the .old file is still there to restore by hand.
                }
            }

            throw new UpdateInstallException("Loadout couldn't replace its files (" + ex.Message + "). It is unchanged.", ex);
        }
    }

    /// <summary>
    /// The new Loadout's tidy-up: deletes the replaced files and the work folder's download. A file still in use
    /// stays listed for next time. Returns whether everything is gone.
    /// </summary>
    public bool CleanUp()
    {
        var list = Path.Combine(WorkFolder, ReplacedList);
        var left = new List<string>();
        if (File.Exists(list))
        {
            foreach (var backup in File.ReadAllLines(list))
            {
                var full = Path.GetFullPath(backup);
                // Only files of ours: inside the app folder, ending in .old.
                if (!full.StartsWith(AppFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || !full.EndsWith(BackupSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!TryDelete(full))
                {
                    left.Add(full);
                }
            }

            try
            {
                if (left.Count > 0)
                {
                    File.WriteAllLines(list, left);
                }
                else
                {
                    File.Delete(list);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                left.Add(list);
            }
        }

        foreach (var folder in new[] { DownloadFolder, StagingFolder })
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                left.Add(folder);
            }
        }

        return left.Count == 0;
    }

    private static bool TryDelete(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
