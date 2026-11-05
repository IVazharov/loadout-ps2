using Loadout.Core;

namespace Loadout.Images;

public sealed class DiscScanOptions
{
    public DiscScanOptions(string cacheDirectory)
    {
        if (string.IsNullOrWhiteSpace(cacheDirectory))
        {
            throw new ArgumentException("A conversion cache directory is required.", nameof(cacheDirectory));
        }

        CacheDirectory = cacheDirectory;
    }

    public string CacheDirectory { get; }

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
        Directory.CreateDirectory(options.CacheDirectory);
        var cache = new ConversionCache(options.CacheDirectory);
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
            progress?.Report(new ScanProgress(index, work.Count, current.SourceFile, null));
            var outcome = await ScanWorkAsync(current, options, cache, progress, index, work.Count, cancellationToken)
                .ConfigureAwait(false);
            items.Add(outcome.Item);
            if (outcome.Cancelled)
            {
                cancelled = true;
                break;
            }
        }

        return new DiscScanResult(ApplyDuplicates(items, installed), cache.LeftoverTemporaryFiles(), cancelled)
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

    /// <param name="incomplete">The discs with only an incomplete install on the PS2, which read as failed until it is removed.</param>
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

    private static async Task<WorkOutcome> ScanWorkAsync(
        WorkItem work,
        DiscScanOptions options,
        ConversionCache cache,
        IProgress<ScanProgress>? progress,
        int index,
        int total,
        CancellationToken cancellationToken)
    {
        if (work.GroupError != null)
        {
            return new WorkOutcome(Failed(work, work.GroupError, MediaKind.Unknown, null), false);
        }

        if (work.ChdPath != null)
        {
            return await ScanChdAsync(work, options, cache, progress, index, total, cancellationToken)
                .ConfigureAwait(false);
        }

        return new WorkOutcome(ScanData(work), false);
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

    private static async Task<WorkOutcome> ScanChdAsync(
        WorkItem work,
        DiscScanOptions options,
        ConversionCache cache,
        IProgress<ScanProgress>? progress,
        int index,
        int total,
        CancellationToken cancellationToken)
    {
        var chdPath = work.ChdPath ?? work.SourceFile;
        ChdFile chd;
        try
        {
            chd = ChdFile.Open(chdPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new WorkOutcome(Failed(work, ex.Message, MediaKind.Unknown, null), false);
        }

        using (chd)
        {
            var kind = chd.Kind;
            if (kind == ChdImageKind.Unknown)
            {
                return new WorkOutcome(
                    Failed(work, "The chd's metadata does not say it is a CD or a DVD. The file name was not used.", MediaKind.Unknown, null),
                    false);
            }

            if (kind == ChdImageKind.Unsupported)
            {
                return new WorkOutcome(
                    Failed(work, "The chd's metadata says it is not a PS2 CD or DVD. The file name was not used.", MediaKind.Unknown, null),
                    false);
            }

            var identity = CacheIdentity.Capture(chdPath, ChdFile.ReaderVersion);
            var cached = cache.TryReuse(identity, kind);
            if (cached != null)
            {
                return new WorkOutcome(FromCache(work, cached), false);
            }

            void Report(double percent) => progress?.Report(new ScanProgress(index, total, chdPath, percent));
            if (kind == ChdImageKind.Dvd)
            {
                return await ExtractDvdAsync(work, options, cache, chd, identity, Report, cancellationToken)
                    .ConfigureAwait(false);
            }

            return await ExtractCdAsync(work, options, cache, chd, identity, Report, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Runs a conversion on a worker thread. Returns why it failed, or null.</summary>
    private static async Task<(bool Cancelled, string? Failure)> ConvertAsync(Action convert, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(convert, cancellationToken).ConfigureAwait(false);
            return (false, null);
        }
        catch (OperationCanceledException)
        {
            return (true, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, ex.Message);
        }
    }

    private static async Task<WorkOutcome> ExtractDvdAsync(
        WorkItem work,
        DiscScanOptions options,
        ConversionCache cache,
        ChdFile chd,
        CacheIdentity identity,
        Action<double> report,
        CancellationToken cancellationToken)
    {
        var chdPath = identity.SourcePath;
        var temp = ChdCacheLayout.IsoTemp(options.CacheDirectory, chdPath);
        var finalPath = ChdCacheLayout.Iso(options.CacheDirectory, chdPath);
        DeleteTemp(temp);
        var (cancelled, failure) = await ConvertAsync(
                () => ChdExtractor.ToIso(chd, temp, report, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
        if (cancelled)
        {
            Abandon(cache, identity, temp, null, ChdImageKind.Dvd);
            return Cancelled(work);
        }

        var facts = failure == null ? Iso9660Image.Read(temp, null, false) : null;
        if (facts == null || !facts.Readable || facts.Corrupt)
        {
            Abandon(cache, identity, temp, null, ChdImageKind.Dvd);
            var detail = failure ?? "The converted ISO failed validation.";
            return new WorkOutcome(Failed(work, detail + " The partial output was not published.", MediaKind.Unknown, null), false);
        }

        PublishTemp(options.CacheDirectory, chdPath, temp, finalPath);
        var published = Iso9660Image.Read(finalPath, null, false);
        WriteValid(cache, identity, finalPath, null, ChdImageKind.Dvd, published, 1);
        var extracted = new WorkItem
        {
            SourceFile = work.SourceFile,
            DataFile = finalPath,
            ChdPath = chdPath,
            RelatedFiles = [chdPath, finalPath],
        };
        return new WorkOutcome(FromFacts(extracted, published, false, null, 1, published.ImageBytes), false);
    }

    private static async Task<WorkOutcome> ExtractCdAsync(
        WorkItem work,
        DiscScanOptions options,
        ConversionCache cache,
        ChdFile chd,
        CacheIdentity identity,
        Action<double> report,
        CancellationToken cancellationToken)
    {
        var chdPath = identity.SourcePath;
        var cueTemp = ChdCacheLayout.CueTemp(options.CacheDirectory, chdPath);
        var binTemp = ChdCacheLayout.BinTemp(options.CacheDirectory, chdPath);
        var cueFinal = ChdCacheLayout.Cue(options.CacheDirectory, chdPath);
        var binFinal = ChdCacheLayout.Bin(options.CacheDirectory, chdPath);
        DeleteTemp(cueTemp);
        DeleteTemp(binTemp);
        var (cancelled, failure) = await ConvertAsync(
                () => ChdExtractor.ToCueAndBin(chd, cueTemp, binTemp, report, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
        if (cancelled)
        {
            Abandon(cache, identity, cueTemp, binTemp, ChdImageKind.Cd);
            return Cancelled(work);
        }

        if (failure != null)
        {
            Abandon(cache, identity, cueTemp, binTemp, ChdImageKind.Cd);
            return new WorkOutcome(
                Failed(work, failure + " The partial output was not published.", MediaKind.Unknown, null),
                false);
        }

        CueSheet sheet;
        try
        {
            sheet = CueSheetParser.Parse(cueTemp);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Abandon(cache, identity, cueTemp, binTemp, ChdImageKind.Cd);
            return new WorkOutcome(Failed(work, ex.Message, MediaKind.Unknown, null), false);
        }

        var decision = CueSheetParser.Evaluate(sheet);
        var facts = Iso9660Image.Read(binTemp, decision.Layout, true);
        if (!facts.Readable || facts.Corrupt)
        {
            Abandon(cache, identity, cueTemp, binTemp, ChdImageKind.Cd);
            return new WorkOutcome(
                Failed(work, "Converted CD image failed validation. The partial output was not published.", MediaKind.Unknown, null),
                false);
        }

        // The sheet names the bin it was written with: name the one it is published as instead.
        File.WriteAllText(
            cueTemp,
            File.ReadAllText(cueTemp).Replace("\"" + Path.GetFileName(binTemp) + "\"", "\"" + Path.GetFileName(binFinal) + "\"", StringComparison.Ordinal));
        PublishTemp(options.CacheDirectory, chdPath, cueTemp, cueFinal);
        PublishTemp(options.CacheDirectory, chdPath, binTemp, binFinal);
        var publishedFacts = Iso9660Image.Read(binFinal, decision.Layout, true);
        WriteValid(cache, identity, cueFinal, binFinal, ChdImageKind.Cd, publishedFacts, sheet.TrackCount);
        var extracted = new WorkItem
        {
            SourceFile = work.SourceFile,
            DataFile = binFinal,
            ChdPath = chdPath,
            Cue = CueSheetParser.Parse(cueFinal),
            RelatedFiles = [chdPath, cueFinal, binFinal],
        };
        return new WorkOutcome(
            FromFacts(extracted, publishedFacts, !decision.Supported, decision.Reason, sheet.TrackCount, new FileInfo(binFinal).Length),
            false);
    }

    private static QueuePreviewItem FromCache(WorkItem work, CacheRecord record)
    {
        if (record.Kind == ChdImageKind.Cd.ToString() && File.Exists(record.OutputPath))
        {
            var sheet = CueSheetParser.Parse(record.OutputPath);
            var decision = CueSheetParser.Evaluate(sheet);
            var data = record.CompanionPath ?? record.OutputPath;
            var facts = Iso9660Image.Read(data, decision.Layout, true);
            var extracted = new WorkItem
            {
                SourceFile = work.SourceFile,
                DataFile = data,
                ChdPath = work.ChdPath,
                Cue = sheet,
                RelatedFiles = work.RelatedFiles,
            };
            return FromFacts(extracted, facts, !decision.Supported, decision.Reason, sheet.TrackCount, new FileInfo(data).Length);
        }

        var isoFacts = Iso9660Image.Read(record.OutputPath, null, false);
        var isoWork = new WorkItem
        {
            SourceFile = work.SourceFile,
            DataFile = record.OutputPath,
            ChdPath = work.ChdPath,
            RelatedFiles = [work.SourceFile, record.OutputPath],
        };
        return FromFacts(isoWork, isoFacts, false, null, 1, isoFacts.ImageBytes);
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

    private static WorkOutcome Cancelled(WorkItem work)
        => new(Failed(work, "Conversion was cancelled.", MediaKind.Unknown, null), true);

    /// <summary>A conversion that stopped cannot be continued, so what it wrote is only taking up room.</summary>
    private static void Abandon(
        ConversionCache cache,
        CacheIdentity identity,
        string outputPath,
        string? companionPath,
        ChdImageKind kind)
    {
        WriteIncomplete(cache, identity, outputPath, companionPath, kind);
        DeleteTemp(outputPath);
        if (companionPath != null)
        {
            DeleteTemp(companionPath);
        }
    }

    private static void WriteIncomplete(
        ConversionCache cache,
        CacheIdentity identity,
        string outputPath,
        string? companionPath,
        ChdImageKind kind)
    {
        cache.Write(new CacheRecord
        {
            SourcePath = identity.SourcePath,
            SourceLength = identity.SourceLength,
            SourceMtimeUtcTicks = identity.SourceMtimeUtcTicks,
            Fingerprint = identity.Fingerprint,
            OutputPath = outputPath,
            CompanionPath = companionPath,
            OutputLength = File.Exists(outputPath) ? new FileInfo(outputPath).Length : 0,
            CompanionLength = companionPath != null && File.Exists(companionPath) ? new FileInfo(companionPath).Length : 0,
            DiscId = null,
            Status = "incomplete",
            ToolVersion = identity.ToolVersion,
            Kind = kind.ToString(),
        });
    }

    private static void WriteValid(
        ConversionCache cache,
        CacheIdentity identity,
        string outputPath,
        string? companionPath,
        ChdImageKind kind,
        ImageFacts facts,
        int trackCount)
    {
        cache.Write(new CacheRecord
        {
            SourcePath = identity.SourcePath,
            SourceLength = identity.SourceLength,
            SourceMtimeUtcTicks = identity.SourceMtimeUtcTicks,
            Fingerprint = identity.Fingerprint,
            OutputPath = outputPath,
            CompanionPath = companionPath,
            OutputLength = new FileInfo(outputPath).Length,
            CompanionLength = companionPath == null ? 0 : new FileInfo(companionPath).Length,
            DiscId = facts.DiscId?.Canonical,
            Status = "valid",
            ToolVersion = identity.ToolVersion,
            Layer0Sectors = facts.Layer0Sectors,
            Layer1Sectors = facts.Layer1Sectors,
            TrackCount = trackCount,
            Kind = kind.ToString(),
        });
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

    private static void PublishTemp(string cacheDirectory, string sourcePath, string temp, string finalPath)
    {
        if (!ChdCacheLayout.IsTemp(temp))
        {
            throw new InvalidOperationException("Refusing to publish a file that is not a conversion temp.");
        }

        var root = Path.GetFullPath(cacheDirectory);
        var target = Path.GetFullPath(finalPath);
        var source = Path.GetFullPath(sourcePath);
        if (string.Equals(target, source, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to overwrite the source image.");
        }

        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to write an image outside the conversion cache.");
        }

        Directory.CreateDirectory(root);
        File.Move(temp, target, overwrite: true);
    }

    private static void DeleteTemp(string path)
    {
        if (!ChdCacheLayout.IsTemp(path))
        {
            throw new InvalidOperationException("Refusing to delete a path that is not a conversion temp.");
        }

        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static bool IsSupported(string path)
        => IsExtension(path, ".iso") || IsExtension(path, ".bin") || IsExtension(path, ".cue") || IsExtension(path, ".chd");

    private static bool IsExtension(string path, string extension)
        => string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase);

    private readonly record struct WorkOutcome(QueuePreviewItem Item, bool Cancelled);

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
