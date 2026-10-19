using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace MeshWeaver.Hosting;

/// <summary>
/// What one directory on the data volume holds: its bytes and files (the whole subtree), and —
/// for a top-level directory — its largest children, so a reading names WHICH identity, module
/// or generation holds the space, not only which store.
/// </summary>
/// <param name="Name">The directory's name relative to the measured root (a top-level entry) or
/// to its top-level parent (a child entry).</param>
/// <param name="Bytes">Bytes of every regular file in the subtree. A FLOOR when
/// <paramref name="UnreadableDirectories"/> is not zero.</param>
/// <param name="Files">Regular files in the subtree.</param>
/// <param name="Children">Immediate child entries (files and directories) of this directory.</param>
/// <param name="NewestWriteUtc">The newest last-write time of any file in the subtree, or null
/// when it holds no file.</param>
/// <param name="UnreadableDirectories">Directories under it that could not be enumerated — what
/// they hold is NOT in <paramref name="Bytes"/>.</param>
/// <param name="SeparateVolume">True when this directory is the mount point of ANOTHER volume
/// (on AKS <c>/data/dumps</c> is its own claim mounted under <c>/data</c>): it is named and not
/// walked, because its bytes are not the measured volume's.</param>
/// <param name="Largest">For a top-level entry: its largest immediate children, biggest first.
/// Empty for a child entry.</param>
public sealed record DataVolumeUsageEntry(
    string Name,
    long Bytes,
    long Files,
    int Children,
    DateTime? NewestWriteUtc,
    int UnreadableDirectories,
    bool SeparateVolume,
    ImmutableList<DataVolumeUsageEntry> Largest);

/// <summary>
/// One walk of a data-volume root: every top-level directory with its subtree's bytes, the loose
/// files beside them, and how the walk itself went.
/// </summary>
/// <param name="Root">The measured root (on AKS <c>/data</c>, the <c>memex-data</c> share).</param>
/// <param name="TakenAtUtc">When the walk finished.</param>
/// <param name="Elapsed">How long it took — on an SMB share a walk is minutes, which is why it
/// runs in the background and never inside a probe.</param>
/// <param name="Directories">Top-level directories, biggest first.</param>
/// <param name="LooseFileBytes">Bytes of regular files directly in <paramref name="Root"/>.</param>
/// <param name="LooseFiles">Regular files directly in <paramref name="Root"/>.</param>
/// <param name="Fault">Why the root itself could not be enumerated, or null.</param>
public sealed record DataVolumeUsageReading(
    string Root,
    DateTimeOffset TakenAtUtc,
    TimeSpan Elapsed,
    ImmutableList<DataVolumeUsageEntry> Directories,
    long LooseFileBytes,
    long LooseFiles,
    string? Fault)
{
    /// <summary>Bytes the walk counted on the measured volume (separate volumes excluded).</summary>
    public long TotalBytes => LooseFileBytes + Directories.Where(d => !d.SeparateVolume).Sum(d => d.Bytes);

    /// <summary>Directories anywhere under the root that could not be enumerated.</summary>
    public int UnreadableDirectories => Directories.Sum(d => d.UnreadableDirectories);
}

/// <summary>
/// The per-directory usage census of the data volume (MeshWeaver#6983).
///
/// <para><b>Why it exists.</b> The free-space signal (<see cref="DataVolumeFreeSpace"/>) says THAT
/// the shared volume is full; nothing said WHAT fills it. On 2026-10-18/19 the 128 GiB
/// <c>memex-data</c> share of the public instance sat at 2–3 MiB free on every replica for 33 hours
/// while four writers share it — <c>modules/</c>, <c>prebuilt-bundles/</c>, <c>assembly-cache/</c>
/// and the CI bake publisher, which writes the share directly and logs nothing on the portal — and
/// their retention reports log at Information, which the cluster does not ship. A fix aimed at one
/// writer (#6969, the assembly store's same-version siblings) shipped without anyone being able to
/// say whether that writer was the one. A per-directory reading is the instrument that decides it,
/// and reading it must not take cluster access: it is published on <c>/health</c>, which the
/// control instance's governed <c>Sample</c> action already captures per replica.</para>
///
/// <para><b>What it measures.</b> Every top-level directory under the root, with its subtree's
/// bytes and file count, newest write, and its largest immediate children (an identity directory
/// under <c>prebuilt-bundles/</c>, a generation under <c>modules/</c>, a NodeType directory under
/// <c>assembly-cache/</c>). A directory that is another volume's mount point is NAMED and not
/// walked. A directory that cannot be enumerated is COUNTED, so a partial walk reads as a floor,
/// never as a clean total.</para>
///
/// <para>Pure over the filesystem it is pointed at: the test drives it on a temp tree.</para>
/// </summary>
public static class DataVolumeUsage
{
    /// <summary>The health-check name.</summary>
    public const string HealthCheckName = "data_volume_usage";

    /// <summary>Config key: how often the walk repeats (a <see cref="TimeSpan"/> string). Default 6 h.</summary>
    public const string IntervalConfigKey = "DataVolume:UsageInterval";

    /// <summary>The default interval: a walk of a large SMB share is minutes of metadata reads, and the
    /// share fills over hours to days, so four readings a day per replica is ample.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromHours(6);

    /// <summary>The interval is clamped to this floor so a typo cannot turn the walk into a loop.</summary>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(15);

    /// <summary>How many of a top-level directory's largest children a reading names.</summary>
    public const int LargestPerDirectory = 5;

    /// <summary>Read the interval from configuration; absent or malformed ⇒ <see cref="DefaultInterval"/>,
    /// and never below <see cref="MinimumInterval"/>.</summary>
    public static TimeSpan IntervalOf(string? configured) =>
        TimeSpan.TryParse(configured, CultureInfo.InvariantCulture, out var interval) && interval > TimeSpan.Zero
            ? (interval < MinimumInterval ? MinimumInterval : interval)
            : DefaultInterval;

    /// <summary>
    /// The roots to walk: the configured data paths with every path that lies INSIDE another
    /// configured path removed, so <c>/data</c> and <c>/data/prebuilt-bundles</c> are one walk of
    /// <c>/data</c> and no byte is counted twice.
    /// </summary>
    public static ImmutableList<string> RootsOf(IEnumerable<string> paths)
    {
        var normalized = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.TrimEndingDirectorySeparator(p.Trim()))
            .Distinct(StringComparer.Ordinal)
            .ToImmutableList();
        return normalized
            .Where(p => !normalized.Any(other =>
                !string.Equals(other, p, StringComparison.Ordinal)
                && p.StartsWith(other + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
            .ToImmutableList();
    }

    /// <summary>The mount points of the volumes this process sees (read from the OS); empty when
    /// they cannot be listed — the walk then simply does not recognise a nested mount.</summary>
    public static ImmutableHashSet<string> MountPoints()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Select(d => Path.TrimEndingDirectorySeparator(d.RootDirectory.FullName))
                .ToImmutableHashSet(StringComparer.Ordinal);
        }
        catch (Exception)
        {
            return ImmutableHashSet<string>.Empty;
        }
    }

    /// <summary>
    /// Walk <paramref name="root"/>. Never throws: an unreadable root is a reading whose
    /// <see cref="DataVolumeUsageReading.Fault"/> says so.
    /// </summary>
    /// <param name="root">The directory to measure.</param>
    /// <param name="mountPoints">Mount points of OTHER volumes; a directory under the root whose
    /// full path is one of them is named and not walked. The root's own mount point is ignored.</param>
    /// <param name="nowUtc">The clock, passed in.</param>
    /// <param name="cancellationToken">Observed between directories.</param>
    public static DataVolumeUsageReading Measure(
        string root,
        ImmutableHashSet<string> mountPoints,
        Func<DateTimeOffset> nowUtc,
        CancellationToken cancellationToken = default)
    {
        var started = nowUtc();
        var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var foreignMounts = mountPoints.Remove(rootPath);
        DirectoryInfo[] directories;
        FileInfo[] looseFiles;
        try
        {
            var info = new DirectoryInfo(rootPath);
            directories = info.GetDirectories();
            looseFiles = info.GetFiles();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new DataVolumeUsageReading(rootPath, nowUtc(), nowUtc() - started,
                ImmutableList<DataVolumeUsageEntry>.Empty, 0, 0, $"{ex.GetType().Name}: {ex.Message}");
        }

        var entries = directories
            .Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0)
            .Select(d => MeasureTopLevel(d, foreignMounts, cancellationToken))
            .OrderByDescending(e => e.Bytes)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToImmutableList();

        return new DataVolumeUsageReading(rootPath, nowUtc(), nowUtc() - started, entries,
            looseFiles.Sum(f => f.Length), looseFiles.Length, null);
    }

    private static DataVolumeUsageEntry MeasureTopLevel(
        DirectoryInfo directory, ImmutableHashSet<string> foreignMounts, CancellationToken cancellationToken)
    {
        if (foreignMounts.Contains(Path.TrimEndingDirectorySeparator(directory.FullName)))
            return new DataVolumeUsageEntry(directory.Name, 0, 0, 0, null, 0, true,
                ImmutableList<DataVolumeUsageEntry>.Empty);

        FileSystemInfo[] children;
        try
        {
            children = directory.GetFileSystemInfos();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new DataVolumeUsageEntry(directory.Name, 0, 0, 0, null, 1, false,
                ImmutableList<DataVolumeUsageEntry>.Empty);
        }

        var childEntries = ImmutableList.CreateBuilder<DataVolumeUsageEntry>();
        long bytes = 0, files = 0;
        var unreadable = 0;
        DateTime? newest = null;
        foreach (var child in children)
        {
            if (cancellationToken.IsCancellationRequested)
                break;
            DataVolumeUsageEntry entry;
            if (child is FileInfo file)
                entry = new DataVolumeUsageEntry(file.Name, file.Length, 1, 0, file.LastWriteTimeUtc, 0, false,
                    ImmutableList<DataVolumeUsageEntry>.Empty);
            else if (child is DirectoryInfo sub && (sub.Attributes & FileAttributes.ReparsePoint) == 0)
                entry = foreignMounts.Contains(Path.TrimEndingDirectorySeparator(sub.FullName))
                    ? new DataVolumeUsageEntry(sub.Name, 0, 0, 0, null, 0, true, ImmutableList<DataVolumeUsageEntry>.Empty)
                    : MeasureSubtree(sub, foreignMounts, cancellationToken);
            else
                continue;
            childEntries.Add(entry);
            bytes += entry.Bytes;
            files += entry.Files;
            unreadable += entry.UnreadableDirectories;
            newest = Newest(newest, entry.NewestWriteUtc);
        }

        var largest = childEntries
            .Where(e => !e.SeparateVolume)
            .OrderByDescending(e => e.Bytes)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .Take(LargestPerDirectory)
            .ToImmutableList();
        return new DataVolumeUsageEntry(directory.Name, bytes, files, children.Length, newest, unreadable, false, largest);
    }

    private static DataVolumeUsageEntry MeasureSubtree(
        DirectoryInfo directory, ImmutableHashSet<string> foreignMounts, CancellationToken cancellationToken)
    {
        long bytes = 0, files = 0;
        var unreadable = 0;
        var immediate = 0;
        DateTime? newest = null;
        var pending = ImmutableStack.Create(directory);
        var first = true;
        while (!pending.IsEmpty)
        {
            if (cancellationToken.IsCancellationRequested)
                break;
            pending = pending.Pop(out var current);
            FileSystemInfo[] children;
            try
            {
                children = current.GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                unreadable++;
                first = false;
                continue;
            }
            if (first)
            {
                immediate = children.Length;
                first = false;
            }
            foreach (var child in children)
            {
                if (child is FileInfo file)
                {
                    bytes += file.Length;
                    files++;
                    newest = Newest(newest, file.LastWriteTimeUtc);
                }
                else if (child is DirectoryInfo sub
                         && (sub.Attributes & FileAttributes.ReparsePoint) == 0
                         && !foreignMounts.Contains(Path.TrimEndingDirectorySeparator(sub.FullName)))
                {
                    pending = pending.Push(sub);
                }
            }
        }
        return new DataVolumeUsageEntry(directory.Name, bytes, files, immediate, newest, unreadable, false,
            ImmutableList<DataVolumeUsageEntry>.Empty);
    }

    private static DateTime? Newest(DateTime? a, DateTime? b) =>
        a is null ? b : b is null ? a : (a > b ? a : b);

    /// <summary>
    /// The one-line publication of a reading — what <c>/health</c> prints. Leads with the
    /// denominator (total, files, how long the walk took, how much it could not read), then every
    /// top-level directory biggest first with its share and its largest children.
    /// </summary>
    public static string Describe(DataVolumeUsageReading? reading)
    {
        if (reading is null)
            return "NOT MEASURED YET on this replica — the first walk of the data volume has not finished "
                + "(it runs in the background after start, and on a large share takes minutes). "
                + "An absent reading is not an empty volume.";
        if (reading.Fault is not null)
            return $"{reading.Root} could not be enumerated at {Stamp(reading.TakenAtUtc)}: {reading.Fault} — nothing was measured.";

        var total = reading.TotalBytes;
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture,
            $"{reading.Root} walked at {Stamp(reading.TakenAtUtc)} in {reading.Elapsed:hh\\:mm\\:ss}: {Mb(total)} in "
            + $"{reading.Directories.Where(d => !d.SeparateVolume).Sum(d => d.Files) + reading.LooseFiles:N0} file(s)");
        text.Append(reading.UnreadableDirectories == 0
            ? ", every directory read"
            : $", {reading.UnreadableDirectories:N0} directory(ies) UNREADABLE — every number below is a FLOOR");
        text.Append(". By directory, largest first: ");
        var parts = new List<string>();
        foreach (var entry in reading.Directories)
        {
            if (entry.SeparateVolume)
            {
                parts.Add($"{entry.Name}/ is another volume's mount point (not counted)");
                continue;
            }
            var share = total == 0 ? 0 : entry.Bytes * 100d / total;
            var line = new StringBuilder();
            line.Append(CultureInfo.InvariantCulture,
                $"{entry.Name}/ {Mb(entry.Bytes)} ({share:F0}%) in {entry.Files:N0} file(s), {entry.Children:N0} entr(ies)");
            if (entry.NewestWriteUtc is { } newest)
                line.Append(CultureInfo.InvariantCulture, $", newest write {newest:yyyy-MM-ddTHH:mm}Z");
            if (!entry.Largest.IsEmpty)
                line.Append(" [largest: ")
                    .Append(string.Join(", ", entry.Largest.Select(c => $"{c.Name} {Mb(c.Bytes)}")))
                    .Append(']');
            parts.Add(line.ToString());
        }
        if (reading.LooseFiles > 0)
            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"{reading.LooseFiles:N0} loose file(s) at the root {Mb(reading.LooseFileBytes)}"));
        text.Append(parts.Count == 0 ? "(the root is empty)" : string.Join("; ", parts));
        return text.ToString();
    }

    private static string Stamp(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) + "Z";

    private static string Mb(long bytes) =>
        (bytes / (1024d * 1024d)).ToString("N0", CultureInfo.InvariantCulture) + " MiB";
}

/// <summary>
/// The latest <see cref="DataVolumeUsageReading"/> per measured root on this replica — a mesh-host
/// singleton (never a static), written by <see cref="DataVolumeUsageHostedService"/> and read by
/// the <c>/health</c> check.
/// </summary>
public sealed class DataVolumeUsageCensus
{
    private ImmutableDictionary<string, DataVolumeUsageReading> latest =
        ImmutableDictionary<string, DataVolumeUsageReading>.Empty.WithComparers(StringComparer.Ordinal);
    private ImmutableDictionary<string, string> faults =
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    /// <summary>The roots this replica measures, set once at start; empty when none is configured.</summary>
    public ImmutableList<string> Roots { get; private set; } = ImmutableList<string>.Empty;

    /// <summary>The latest reading of each root that has been walked at least once.</summary>
    public ImmutableDictionary<string, DataVolumeUsageReading> Latest => Volatile.Read(ref latest);

    /// <summary>The fault of each root's latest pass, when that pass faulted.</summary>
    public ImmutableDictionary<string, string> Faults => Volatile.Read(ref faults);

    /// <summary>Declare the roots this replica measures.</summary>
    public void SetRoots(ImmutableList<string> roots) => Roots = roots;

    /// <summary>Record a finished walk; clears that root's fault.</summary>
    public void Record(DataVolumeUsageReading reading)
    {
        ImmutableInterlocked.Update(ref latest, d => d.SetItem(reading.Root, reading));
        ImmutableInterlocked.Update(ref faults, d => d.Remove(reading.Root));
    }

    /// <summary>Record a pass that faulted; the previous reading (if any) stays and is labelled stale by the fault.</summary>
    public void RecordFault(string root, Exception exception) =>
        ImmutableInterlocked.Update(ref faults, d => d.SetItem(root, $"{exception.GetType().Name}: {exception.Message}"));

    /// <summary>The publication: one sentence per configured root, or why there is none.</summary>
    public string Describe()
    {
        if (Roots.IsEmpty)
            return "no data volume configured (Modules:Root / PreWarm:PrebuiltBundleRoot / DataVolume:Paths) — nothing to measure";
        var current = Latest;
        var currentFaults = Faults;
        return string.Join(" | ", Roots.Select(root =>
        {
            current.TryGetValue(root, out var reading);
            var text = DataVolumeUsage.Describe(reading);
            return currentFaults.TryGetValue(root, out var fault)
                ? $"{text} — the LATEST pass over {root} FAULTED ({fault}), so this reading is the previous one"
                : text;
        }));
    }

    /// <summary>True when a configured root's latest pass faulted or its walk could not read the root.</summary>
    public bool IsDegraded =>
        !Faults.IsEmpty || Latest.Values.Any(r => r.Fault is not null);
}
