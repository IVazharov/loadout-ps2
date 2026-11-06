using Loadout.Core;

namespace Loadout.Images;

public sealed class DiscScanOptions
{
    /// <summary>Images already in the queue: a scan does not read them again.</summary>
    public IReadOnlySet<string>? Queued { get; init; }
}

public static class DiscScanner
{
    public static async Task<DiscScanResult> ScanAsync(
        IReadOnlyList<ScanInput> inputs,
        InstalledGameIndex? installed,
        DiscScanOptions options,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(options);
        var items = new List<QueuePreviewItem>();
        var work = Collect(inputs, items);
        var alreadyQueued = new List<string>();
        if (options.Queued is { } queued)
        {
            alreadyQueued.AddRange(work.Select(item => item.SourceFile).Where(queued.Contains));
            work.RemoveAll(item => queued.Contains(item.SourceFile));
        }

        var cancelled = false;
        for (var index = 0; index < work.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            var current = work[index];
            progress?.Report(new ScanProgress(index, work.Count, current.SourceFile));

            // Reading a large chd's map takes a moment, so no image is read on the caller's thread.
            items.Add(await Task.Run(() => ScanWork(current), CancellationToken.None).ConfigureAwait(false));
        }

        return new DiscScanResult(ApplyDuplicates(items, installed), cancelled)
        {
            AlreadyQueued = alreadyQueued,
        };
    }

    /// <summary>
    /// Splits newly scanned items into those to add to a queue and those to leave out: a ready disc
    /// already on the PS2, already in <paramref name="queued"/>, or met earlier among <paramref name="added"/>.
    /// A left-out item carries the duplicate status that says which.
    /// </summary>
    public static (IReadOnlyList<QueuePreviewItem> Kept, IReadOnlyList<QueuePreviewItem> LeftOut) WithoutDuplicates(
        IReadOnlyList<QueuePreviewItem> queued,
        IReadOnlyList<QueuePreviewItem> added,
        InstalledGameIndex? installed)
    {
        var seen = new HashSet<string>(queued.Select(item => item.DiscId).OfType<string>(), StringComparer.Ordinal);
        var kept = new List<QueuePreviewItem>(added.Count);
        var leftOut = new List<QueuePreviewItem>();
        foreach (var item in added)
        {
            if (item.DiscId != null && item.Preliminary == QueueItemStatus.Ready)
            {
                var inLibrary = installed?.TryGet(item.DiscId, out _) == true;
                if (inLibrary || seen.Contains(item.DiscId))
                {
                    leftOut.Add(item.Resolve(inLibrary, !inLibrary));
                    continue;
                }
            }

            if (item.DiscId != null)
            {
                seen.Add(item.DiscId);
            }

            kept.Add(item);
        }

        return (kept, leftOut);
    }

    /// <param name="incomplete">The discs with only an incomplete install on the PS2, which installing replaces.</param>
    public static IReadOnlyList<QueuePreviewItem> ApplyDuplicates(
        IReadOnlyList<QueuePreviewItem> items,
        InstalledGameIndex? installed,
        InstalledGameIndex? incomplete = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var resolved = new List<QueuePreviewItem>(items.Count);
        foreach (var item in items.OrderBy(candidate => candidate.SourceFile, StringComparer.Ordinal))
        {
            var inLibrary = false;
            var inQueue = false;
            var incompleteOnPs2 = false;
            if (item.DiscId != null)
            {
                inLibrary = installed?.TryGet(item.DiscId, out _) == true;
                inQueue = !inLibrary && !seen.Add(item.DiscId);
                incompleteOnPs2 = incomplete?.TryGet(item.DiscId, out _) == true;
                if (inLibrary)
                {
                    seen.Add(item.DiscId);
                }
            }

            resolved.Add(item.Resolve(inLibrary, inQueue, incompleteOnPs2));
        }

        return resolved;
    }

    private static QueuePreviewItem ScanWork(WorkItem work)
    {
        if (work.GroupError != null)
        {
            return Failed(work, work.GroupError, MediaKind.Unknown, null);
        }

        return work.ChdPath != null ? ScanChd(work) : ScanData(work);
    }

    private static QueuePreviewItem ScanData(WorkItem work)
    {
        SectorLayout? layout = null;
        var cueUnsupported = false;
        string? cueReason = null;
        var declaredCd = false;
        var trackCount = 1;
        if (work.Cue != null)
        {
            var decision = CueSheetParser.Evaluate(work.Cue);
            layout = decision.Layout;
            cueUnsupported = !decision.Supported;
            cueReason = decision.Reason;
            declaredCd = true;
            trackCount = work.Cue.TrackCount;
            if (work.DataFile == null || !File.Exists(work.DataFile))
            {
                return Failed(work, "CUE references a missing image.", MediaKind.CdMultiTrack, null);
            }
        }
        else if (work.DataFile == null || !File.Exists(work.DataFile))
        {
            return Failed(work, "Image file was not found.", MediaKind.Unknown, null);
        }

        var facts = Iso9660Image.Read(work.DataFile, layout, declaredCd);
        return FromFacts(work, facts, cueUnsupported, cueReason, trackCount, SizeOf(work, facts));
    }

    /// <summary>
    /// Reads a chd as the iso, or the cue sheet and bin, that chdman extracts from it, without writing
    /// them: the chd itself is the image the install reads.
    /// </summary>
    private static QueuePreviewItem ScanChd(WorkItem work)
    {
        var chdPath = work.ChdPath ?? work.SourceFile;
        try
        {
            var chd = ChdFile.Open(chdPath);
            var kind = chd.Kind;
            if (kind is ChdImageKind.Unknown or ChdImageKind.Unsupported)
            {
                chd.Dispose();
                return Failed(
                    work,
                    kind == ChdImageKind.Unknown
                        ? "The chd's metadata does not say it is a CD or a DVD. The file name was not used."
                        : "The chd's metadata says it is not a PS2 CD or DVD. The file name was not used.",
                    MediaKind.Unknown,
                    null);
            }

            ChdImageStream image;
            try
            {
                image = ChdImageStream.Open(chd);
            }
            catch
            {
                chd.Dispose();
                throw;
            }

            using (image)
            {
                var item = new WorkItem
                {
                    SourceFile = work.SourceFile,
                    DataFile = chdPath,
                    ChdPath = chdPath,
                    RelatedFiles = [chdPath],
                };
                if (image.Tracks.Count == 0)
                {
                    return FromFacts(item, Iso9660Image.Read(image, chdPath, null, false), false, null, 1, image.Length);
                }

                // The sheet chdman would write, naming the chd as its bin.
                var sheet = CueSheetParser.Parse(
                    ChdTracks.Sheet(image.Tracks, Path.GetFileName(chdPath)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries),
                    chdPath);
                var decision = CueSheetParser.Evaluate(sheet);
                var facts = Iso9660Image.Read(image, chdPath, decision.Layout, true);
                var cd = new WorkItem
                {
                    SourceFile = item.SourceFile,
                    DataFile = item.DataFile,
                    ChdPath = item.ChdPath,
                    Cue = sheet,
                    RelatedFiles = item.RelatedFiles,
                };
                return FromFacts(cd, facts, !decision.Supported, decision.Reason, sheet.TrackCount, image.Length);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Failed(work, ex.Message, MediaKind.Unknown, null);
        }
    }

    private static QueuePreviewItem FromFacts(
        WorkItem work,
        ImageFacts facts,
        bool cueUnsupported,
        string? cueReason,
        int trackCount,
        long? convertedSize)
    {
        var unsupported = cueUnsupported || facts.TrailingNonDataSectors;
        var reason = cueUnsupported
            ? cueReason
            : facts.TrailingNonDataSectors ? ImageLimits.MultiTrack : facts.Failure;
        var media = facts.Media;
        if (unsupported && media != MediaKind.Dvd9)
        {
            media = MediaKind.CdMultiTrack;
        }

        var readable = facts.Readable && !facts.Corrupt;
        string? title;
        var fallback = false;
        if (readable && !string.IsNullOrWhiteSpace(facts.VolumeLabel))
        {
            title = facts.VolumeLabel;
        }
        else if (readable && facts.DiscId != null && !facts.AmbiguousId)
        {
            title = facts.DiscId.Value.Canonical;
        }
        else
        {
            title = Path.GetFileNameWithoutExtension(work.SourceFile);
            fallback = true;
        }

        QueueItemStatus status;
        string? detail;
        if (!facts.Readable || facts.Corrupt)
        {
            status = QueueItemStatus.Failed;
            detail = facts.Failure ?? "The image could not be read.";
            media = facts.Readable ? media : MediaKind.Unknown;
        }
        else if (unsupported)
        {
            status = QueueItemStatus.Failed;
            detail = reason ?? ImageLimits.MultiTrack;
        }
        else if (facts.AmbiguousId)
        {
            status = QueueItemStatus.NeedsMetadataReview;
            detail = "Ambiguous disc ID in SYSTEM.CNF.";
        }
        else if (facts.DiscId == null)
        {
            status = QueueItemStatus.NeedsMetadataReview;
            detail = "Unidentified: SYSTEM.CNF did not contain a disc ID.";
        }
        else
        {
            status = QueueItemStatus.Ready;
            detail = null;
        }

        return Make(work, status, detail, facts, fallback, title, media, convertedSize, trackCount);
    }

    private static QueuePreviewItem Make(
        WorkItem work,
        QueueItemStatus status,
        string? detail,
        ImageFacts? facts,
        bool filenameFallback,
        string? detectedTitle,
        MediaKind media,
        long? convertedSize,
        int trackCount)
    {
        var fitted = TitleText.FitQueueTitle(detectedTitle, facts?.DiscId?.Canonical);
        var setSize = facts?.VolumeSetSize ?? 0;
        var sequence = facts?.VolumeSequence ?? 0;
        return new QueuePreviewItem
        {
            SourceFile = work.SourceFile,
            DataFile = work.DataFile,
            RelatedFiles = work.RelatedFiles,
            DiscId = facts?.DiscId?.Canonical,
            Region = facts?.DiscId?.Region,
            DetectedTitle = fitted,
            TitleIsFilenameFallback = filenameFallback,
            Media = media,
            MediaLabel = QueuePreviewItem.DescribeMedia(media, setSize, sequence),
            Layer0Sectors = facts?.Layer0Sectors ?? 0,
            Layer1Sectors = facts?.Layer1Sectors ?? 0,
            HdlDiscType = DiscType(media),
            VolumeSetSize = setSize,
            VolumeSequence = sequence,
            ConvertedSize = convertedSize,
            Preliminary = status,
            Status = status,
            Detail = detail,
            TrackCount = trackCount,
        };
    }

    private static QueuePreviewItem Failed(WorkItem work, string detail, MediaKind media, long? convertedSize)
    {
        var title = Path.GetFileNameWithoutExtension(work.SourceFile);
        return Make(work, QueueItemStatus.Failed, detail, null, true, title, media, convertedSize, 0);
    }

    private static List<WorkItem> Collect(IReadOnlyList<ScanInput> inputs, List<QueuePreviewItem> errors)
    {
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            if (string.IsNullOrWhiteSpace(input.Path))
            {
                continue;
            }

            var full = Path.GetFullPath(input.Path);
            if (input.Depth == ScanDepth.File)
            {
                if (!File.Exists(full))
                {
                    errors.Add(Missing(full));
                    continue;
                }

                if (!IsSupported(full))
                {
                    errors.Add(Missing(full, "Unsupported file type."));
                    continue;
                }

                files.Add(full);
                continue;
            }

            if (!Directory.Exists(full))
            {
                errors.Add(Missing(full));
                continue;
            }

            var enumeration = new EnumerationOptions
            {
                RecurseSubdirectories = input.Depth == ScanDepth.DirectoryRecursive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            foreach (var file in Directory.EnumerateFiles(full, "*", enumeration))
            {
                if (IsSupported(file))
                {
                    files.Add(Path.GetFullPath(file));
                }
            }
        }

        return Group(files);
    }

    private static List<WorkItem> Group(IReadOnlyCollection<string> selected)
    {
        var cues = new HashSet<string>(selected.Where(path => IsExtension(path, ".cue")), StringComparer.Ordinal);
        foreach (var bin in selected.Where(path => IsExtension(path, ".bin")))
        {
            var directory = Path.GetDirectoryName(bin);
            if (string.IsNullOrEmpty(directory))
            {
                continue;
            }

            IEnumerable<string> neighbours;
            try
            {
                neighbours = Directory.EnumerateFiles(directory, "*.cue");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var neighbour in neighbours)
            {
                var full = Path.GetFullPath(neighbour);
                if (!cues.Contains(full) && CueSheetParser.References(full, bin))
                {
                    cues.Add(full);
                }
            }
        }

        var consumed = new HashSet<string>(StringComparer.Ordinal);
        var work = new List<WorkItem>();
        foreach (var cuePath in cues.Order(StringComparer.Ordinal))
        {
            CueSheet sheet;
            try
            {
                sheet = CueSheetParser.Parse(cuePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                work.Add(new WorkItem
                {
                    SourceFile = cuePath,
                    GroupError = ex.Message,
                    RelatedFiles = [cuePath],
                });
                continue;
            }

            if (sheet.Files.Count == 0)
            {
                work.Add(new WorkItem
                {
                    SourceFile = cuePath,
                    GroupError = "CUE sheet has no FILE entry.",
                    RelatedFiles = [cuePath],
                });
                continue;
            }

            foreach (var file in sheet.Files)
            {
                consumed.Add(file.Path);
            }

            var related = new List<string> { cuePath };
            related.AddRange(sheet.Files.Select(file => file.Path));
            work.Add(new WorkItem
            {
                SourceFile = cuePath,
                DataFile = DataFileOf(sheet),
                Cue = sheet,
                RelatedFiles = related,
            });
        }

        foreach (var path in selected.Order(StringComparer.Ordinal))
        {
            if (IsExtension(path, ".cue") || consumed.Contains(path))
            {
                continue;
            }

            if (IsExtension(path, ".chd"))
            {
                work.Add(new WorkItem
                {
                    SourceFile = path,
                    DataFile = path,
                    ChdPath = path,
                    RelatedFiles = [path],
                });
            }
            else if (IsExtension(path, ".iso") || IsExtension(path, ".bin"))
            {
                work.Add(new WorkItem
                {
                    SourceFile = path,
                    DataFile = path,
                    RelatedFiles = [path],
                });
            }
        }

        return work;
    }

    private static string? DataFileOf(CueSheet sheet)
    {
        foreach (var file in sheet.Files)
        {
            if (file.Tracks.Any(track => CueSheetParser.LayoutOf(track.Mode) != null))
            {
                return file.Path;
            }
        }

        return sheet.Files.Count > 0 ? sheet.Files[0].Path : null;
    }

    private static long? SizeOf(WorkItem work, ImageFacts facts)
    {
        long sum = 0;
        var any = false;
        foreach (var path in work.RelatedFiles)
        {
            if (IsExtension(path, ".cue") || IsExtension(path, ".chd"))
            {
                continue;
            }

            if (!File.Exists(path))
            {
                continue;
            }

            sum += new FileInfo(path).Length;
            any = true;
        }

        return any ? sum : facts.ImageBytes;
    }

    private static QueuePreviewItem Missing(string path, string detail = "Path was not found.")
        => Make(
            new WorkItem { SourceFile = path, RelatedFiles = [path] },
            QueueItemStatus.Failed,
            detail,
            null,
            true,
            Path.GetFileNameWithoutExtension(path),
            MediaKind.Unknown,
            null,
            0);

    private static byte DiscType(MediaKind media) => media switch
    {
        MediaKind.Cd => HdlDiscTypes.Ps2Cd,
        MediaKind.CdMultiTrack => HdlDiscTypes.Ps2CdDa,
        MediaKind.Dvd5 => HdlDiscTypes.Ps2Dvd,
        MediaKind.Dvd9 => HdlDiscTypes.Ps2Dvd,
        _ => 0,
    };

    private static bool IsSupported(string path)
        => IsExtension(path, ".iso") || IsExtension(path, ".bin") || IsExtension(path, ".cue") || IsExtension(path, ".chd");

    private static bool IsExtension(string path, string extension)
        => string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase);

    private sealed class WorkItem
    {
        public required string SourceFile { get; init; }

        public string? DataFile { get; init; }

        public string? ChdPath { get; init; }

        public CueSheet? Cue { get; init; }

        public IReadOnlyList<string> RelatedFiles { get; init; } = Array.Empty<string>();

        public string? GroupError { get; init; }
    }
}
