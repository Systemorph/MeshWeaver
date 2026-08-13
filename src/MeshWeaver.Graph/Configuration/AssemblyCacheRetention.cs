using System.Collections.Immutable;
using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// How much of the shared assembly cache to keep, and whether the sweep is allowed to delete.
///
/// <para><b>Every default here is chosen to keep, not to collect.</b> Deleting a DLL out from under a
/// process that is about to load it is fatal and irreversible, while keeping one costs a few tens of
/// megabytes on a 16 GiB share — so every knob is a KEEP rule and they are ORed together: a
/// generation survives if ANY of them protects it.</para>
/// </summary>
public sealed record AssemblyCacheRetention
{
    /// <summary>
    /// The shipped default: measure and report, delete nothing (<see cref="Delete"/> is
    /// <c>false</c>). A deployment arms collection explicitly, after reading a report.
    /// </summary>
    public static readonly AssemblyCacheRetention ReportOnly = new();

    /// <summary>
    /// How many generations to keep purely because they are the most recently written, regardless of
    /// whether anything claims them. Three is live + previous + one, which makes a rollback cheap and
    /// — the case that actually matters — protects the OUTGOING image's generation during the very
    /// rollout that introduces claim-writing, when the pod still serving on the previous image is by
    /// construction not yet writing a claim for it.
    /// </summary>
    public int KeepGenerations { get; init; } = 3;

    /// <summary>
    /// A generation is never collected until its newest file is at least this old. A pure backstop
    /// under <see cref="KeepGenerations"/> and the claims: it bounds the damage a wrong answer from
    /// either can do to "something nobody has written to in a week".
    /// </summary>
    public TimeSpan MinimumAge { get; init; } = TimeSpan.FromDays(7);

    /// <summary>
    /// How long a generation claim counts as evidence that some pod is still running that framework.
    /// Deliberately many multiples of <see cref="ClaimRefreshInterval"/>: a claim that is merely LATE
    /// must never read as a claim that is GONE, because only the second one deletes anything.
    /// </summary>
    public TimeSpan ClaimTtl { get; init; } = TimeSpan.FromHours(24);

    /// <summary>How often a live process re-asserts the claim on the framework generation it runs.</summary>
    public TimeSpan ClaimRefreshInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Whether the sweep may actually delete. <b>Default false</b> — it computes and logs exactly what
    /// it WOULD remove and then removes nothing. Arming this is a deployment decision taken against a
    /// report, never a side effect of upgrading.
    /// </summary>
    public bool Delete { get; init; }
}

/// <summary>
/// One framework generation's footprint in the cache: every file whose name carries this framework
/// tag, across every NodeType directory.
/// </summary>
/// <param name="Tag">The 8-char framework tag from the filename.</param>
/// <param name="Files">Full paths of the files that belong to this generation (DLLs and PDBs).</param>
/// <param name="Bytes">Total size of <paramref name="Files"/>.</param>
/// <param name="NewestWriteUtc">Last-write time of the most recently written file in the generation.</param>
public sealed record AssemblyCacheGeneration(
    string Tag,
    ImmutableList<string> Files,
    long Bytes,
    DateTimeOffset NewestWriteUtc)
{
    /// <summary>Number of files in this generation.</summary>
    public int FileCount => Files.Count;
}

/// <summary>
/// A live process's assertion "I am running framework <see cref="Tag"/>". This — not a file's age and
/// not a count — is what proves a generation is still referenced.
/// </summary>
/// <param name="Tag">The framework generation the holder is running.</param>
/// <param name="Holder">Who wrote it (the pod / machine name).</param>
/// <param name="AtUtc">
/// When it was last asserted, read from the file's CONTENT. <c>null</c> means the claim file exists
/// but could not be read or parsed — which PROTECTS the generation: a claim we cannot evaluate is
/// never evidence that nothing holds it.
/// </param>
public sealed record AssemblyCacheClaim(string Tag, string Holder, DateTimeOffset? AtUtc);

/// <summary>What a sweep found and what it decided, before any deletion happens.</summary>
/// <param name="LiveTag">The framework generation the sweeping process itself is running.</param>
/// <param name="Generations">Every generation present in the cache.</param>
/// <param name="Claims">Every claim file found (fresh, stale and unreadable alike).</param>
/// <param name="Collectable">The generations the retention rules allow to be deleted.</param>
/// <param name="Protected">Why each surviving generation survived, keyed by tag.</param>
/// <param name="UnrecognisedFiles">Files under the cache root whose names the store did not write (never collected).</param>
/// <param name="UnrecognisedBytes">Total size of <paramref name="UnrecognisedFiles"/>.</param>
public sealed record AssemblyCacheSweepPlan(
    string LiveTag,
    ImmutableList<AssemblyCacheGeneration> Generations,
    ImmutableList<AssemblyCacheClaim> Claims,
    ImmutableList<AssemblyCacheGeneration> Collectable,
    ImmutableDictionary<string, string> Protected,
    int UnrecognisedFiles,
    long UnrecognisedBytes)
{
    /// <summary>Total bytes across every generation (excludes <see cref="UnrecognisedBytes"/>).</summary>
    public long TotalBytes => Generations.Sum(g => g.Bytes);

    /// <summary>Total files across every generation.</summary>
    public int TotalFiles => Generations.Sum(g => g.FileCount);

    /// <summary>Bytes the plan would reclaim.</summary>
    public long CollectableBytes => Collectable.Sum(g => g.Bytes);

    /// <summary>One line an operator can read at 3am.</summary>
    public string Summary =>
        $"{Generations.Count} generation(s) / {TotalFiles} file(s) / {Mb(TotalBytes)} — "
        + $"live={LiveTag}, collectable={Collectable.Count} generation(s) / "
        + $"{Collectable.Sum(g => g.FileCount)} file(s) / {Mb(CollectableBytes)}"
        + (UnrecognisedFiles == 0 ? "" : $", unrecognised={UnrecognisedFiles} file(s) (never collected)");

    private static string Mb(long bytes) =>
        (bytes / (1024d * 1024d)).ToString("N1", CultureInfo.InvariantCulture) + " MB";
}

/// <summary>The outcome of a sweep: what it planned, and what (if anything) it actually removed.</summary>
/// <param name="Plan">The plan, or <c>null</c> when the sweep aborted before forming one.</param>
/// <param name="Deleted">Whether deletion was armed AND performed.</param>
/// <param name="DeletedFiles">Files actually removed.</param>
/// <param name="DeletedBytes">Bytes actually reclaimed.</param>
/// <param name="FailedDeletes">Files the sweep tried and failed to remove (they stay; the next sweep re-plans them).</param>
/// <param name="AbortReason">Why the sweep refused to collect anything, or <c>null</c> when it ran normally.</param>
public sealed record AssemblyCacheSweepResult(
    AssemblyCacheSweepPlan? Plan,
    bool Deleted,
    int DeletedFiles,
    long DeletedBytes,
    int FailedDeletes,
    string? AbortReason);

/// <summary>
/// 🚨 THE ASSEMBLY CACHE GROWS BY ONE WHOLE GENERATION PER DEPLOY, AND NOTHING USED TO REMOVE ONE.
///
/// <para><b>Why it grows.</b> <see cref="FileSystemAssemblyStore"/> keys every file
/// <c>v{version}-{frameworkTag}-{contentHash}.dll</c>, where the tag is the first 8 chars of the
/// <c>MeshWeaver.Graph</c> MVID. Under <c>CIRun</c> the build stamps a fresh
/// <c>InformationalVersion</c> into every assembly, so Graph's MVID — and therefore the tag —
/// changes on EVERY published build whether or not any source changed. That is deliberate, load-
/// bearing ABI safety (a new image must never load the previous image's bytes: prod 2026-06-20,
/// <c>BadImageFormatException</c> → failed grain activations → portal-wide wedge). So a fresh
/// generation of the whole fleet is correct; what was missing is anything that ever removes one.
/// Measured on memex 2026-08-12: 7817 DLLs across 93 generations, 3.2 GB — of which 83 files (1%)
/// were loadable by the running image.</para>
///
/// <para><b>Why that is urgent rather than untidy.</b> The share is not dedicated to this cache. On
/// AKS <c>/data</c> is a 16 GiB Azure Files volume that ALSO holds the DataProtection key ring
/// (<c>/data/dataprotection-keys</c>), the NuGet package cache and the Graph storage base path, so
/// filling it takes out auth-adjacent state at the same moment as the compile cache — and a full
/// SMB share fails writes in ways reported far from the cause.</para>
///
/// <para><b>🚨 What proves a generation is unreferenced: a live CLAIM, not an age and not a count.</b>
/// A pod's generation is fixed for its whole life (it is its image's MVID), and a fully warm pod can
/// go days without touching the share — so file age says nothing at all about whether a generation
/// is in use. Every process that owns a filesystem assembly cache therefore re-asserts
/// <c>{root}/.generations/{tag}/{holder}</c> on <see cref="AssemblyCacheRetention.ClaimRefreshInterval"/>,
/// and a generation any live claim names is NEVER collected. The claim's instant lives in the file's
/// CONTENT, not in its last-write metadata, deliberately: SMB metadata caching can make a timestamp
/// read stale when it is not, and here a falsely-stale read is the one error that deletes something.</para>
///
/// <para><b>Every rule is a KEEP rule, and they are ORed.</b> A generation survives if it is the
/// sweeping process's own live tag, OR any claim within <see cref="AssemblyCacheRetention.ClaimTtl"/>
/// names it, OR it is among the <see cref="AssemblyCacheRetention.KeepGenerations"/> most recently
/// written, OR its newest file is younger than <see cref="AssemblyCacheRetention.MinimumAge"/>. The
/// claim's failure mode is a pod whose claim writes fail for longer than the TTL; the recency and age
/// rules are what bound that, and they also cover the rollout that first introduces claims, where the
/// outgoing image is not writing one yet. The claims' failure mode is exactly why plain count/age
/// retention is not enough on its own: a pod that has not rolled in longer than the window is
/// invisible to count/age, with no signal at all.</para>
///
/// <para><b>What it will never touch.</b> The sweep is rooted at the assembly-cache directory (a
/// SIBLING of the DataProtection key directory, never an ancestor), descends exactly one level into
/// the per-NodeType directories, and deletes only files whose name parses as this store's
/// <c>v{version}-{tag}-{hash}.dll|.pdb</c>. A name it cannot attribute to a generation — including
/// the bake-lease files, the claim files, and any pre-tag legacy DLL — is counted and never deleted.
/// Any error reading the tree or the claims ABORTS the sweep with nothing collected: an incomplete
/// picture must never license a deletion.</para>
/// </summary>
public static class AssemblyCacheGenerations
{
    /// <summary>Directory under the cache root that holds the per-generation claim files.</summary>
    public const string ClaimsDirectoryName = ".generations";

    /// <summary>
    /// Width of the framework tag the store writes — <c>FrameworkVersion[..8]</c>, see
    /// <see cref="FileSystemAssemblyStore.FrameworkTag"/>.
    /// </summary>
    private const int FrameworkTagLength = 8;

    /// <summary>
    /// Width of the content hash the store writes — 12 hex chars (<c>ToHexString</c> of the first
    /// 6 SHA-256 bytes), see <c>FileSystemAssemblyStore.ContentHash</c>.
    /// </summary>
    private const int ContentHashLength = 12;

    /// <summary>
    /// The framework generation a cache filename belongs to, or <c>null</c> when the name is not one
    /// this store wrote. Strict on purpose — everything this refuses is something the sweep will
    /// never delete, so the shape it accepts IS the deletion boundary.
    /// </summary>
    public static string? TagOf(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        if (!string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".pdb", StringComparison.OrdinalIgnoreCase))
            return null;

        // v{version}-{frameworkTag}-{contentHash} — exactly three segments, no more, no less. A
        // pre-tag legacy name (v{version}-{hash}) has two and is therefore never attributed, so it
        // is never collected either.
        var parts = Path.GetFileNameWithoutExtension(fileName).Split('-');
        if (parts.Length != 3)
            return null;
        if (parts[0].Length < 2 || (parts[0][0] != 'v' && parts[0][0] != 'V'))
            return null;
        if (!long.TryParse(parts[0].AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            return null;
        // 🚨 The WIDTHS are part of the shape, not decoration. The store always emits an 8-char tag
        // and a 12-char hash, so accepting any hex length would let a foreign name like
        // `v1-ab-cd.dll` be attributed to a generation — and attribution is what makes a file
        // deletable. Matching exactly what the writer emits is what keeps "only files this store
        // wrote are ever deleted" literally true.
        return parts[1].Length == FrameworkTagLength && IsHex(parts[1])
               && parts[2].Length == ContentHashLength && IsHex(parts[2])
            ? parts[1].ToLowerInvariant()
            : null;
    }

    private static bool IsHex(string s) =>
        s.Length > 0 && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    /// <summary>
    /// Enumerate the cache: one entry per framework generation, plus a count of the files whose names
    /// this store did not write. Descends exactly ONE level (the per-NodeType directories) and skips
    /// <see cref="ClaimsDirectoryName"/> — the layout is flat by construction
    /// (<c>{root}/{sanitized-nodeTypePath}/…</c>), so recursion could only ever reach something that
    /// is not ours.
    /// </summary>
    public static (ImmutableList<AssemblyCacheGeneration> Generations, int UnrecognisedFiles, long UnrecognisedBytes)
        Scan(string rootDirectory)
    {
        var files = new Dictionary<string, List<FileInfo>>(StringComparer.OrdinalIgnoreCase);
        var unrecognised = 0;
        var unrecognisedBytes = 0L;

        var root = new DirectoryInfo(rootDirectory);
        if (!root.Exists)
            return (ImmutableList<AssemblyCacheGeneration>.Empty, 0, 0);

        foreach (var typeDirectory in root.EnumerateDirectories())
        {
            if (string.Equals(typeDirectory.Name, ClaimsDirectoryName, StringComparison.Ordinal))
                continue;
            foreach (var file in typeDirectory.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
            {
                var tag = TagOf(file.Name);
                if (tag is null)
                {
                    unrecognised++;
                    unrecognisedBytes += file.Length;
                    continue;
                }
                if (!files.TryGetValue(tag, out var bucket))
                    files[tag] = bucket = [];
                bucket.Add(file);
            }
        }

        var generations = files
            .Select(kvp => new AssemblyCacheGeneration(
                kvp.Key,
                kvp.Value.Select(f => f.FullName).ToImmutableList(),
                kvp.Value.Sum(f => f.Length),
                kvp.Value.Max(f => new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero))))
            .OrderByDescending(g => g.NewestWriteUtc)
            .ToImmutableList();

        return (generations, unrecognised, unrecognisedBytes);
    }

    /// <summary>
    /// Read every generation claim under the cache root. A claim file that exists but cannot be read
    /// or parsed comes back with a <c>null</c> instant, which PROTECTS its generation — see
    /// <see cref="AssemblyCacheClaim.AtUtc"/>.
    /// </summary>
    public static ImmutableList<AssemblyCacheClaim> ReadClaims(string rootDirectory)
    {
        var claimsRoot = new DirectoryInfo(Path.Combine(rootDirectory, ClaimsDirectoryName));
        if (!claimsRoot.Exists)
            return ImmutableList<AssemblyCacheClaim>.Empty;

        var claims = ImmutableList.CreateBuilder<AssemblyCacheClaim>();
        foreach (var tagDirectory in claimsRoot.EnumerateDirectories())
            foreach (var file in tagDirectory.EnumerateFiles("*", SearchOption.TopDirectoryOnly))
            {
                DateTimeOffset? at;
                try
                {
                    at = ParseClaim(File.ReadAllText(file.FullName));
                }
                catch (Exception)
                {
                    // 🚨 EVERY read failure, not just IOException — a denied ACL
                    // (UnauthorizedAccessException) is every bit as much "I cannot evaluate this
                    // claim" as a locked file, and letting it escape would abort the whole sweep
                    // over one claim file. Unreadable RIGHT NOW is not "gone": a null instant
                    // PROTECTS the generation, which is the conservative direction and exactly what
                    // AssemblyCacheClaim.AtUtc promises.
                    at = null;
                }
                claims.Add(new AssemblyCacheClaim(
                    tagDirectory.Name.ToLowerInvariant(), file.Name, at));
            }
        return claims.ToImmutable();
    }

    /// <summary>
    /// Decide what may be collected. Pure — no filesystem access, no clock — so the rules are
    /// testable exactly as they are enforced.
    /// </summary>
    public static AssemblyCacheSweepPlan Plan(
        string liveTag,
        ImmutableList<AssemblyCacheGeneration> generations,
        ImmutableList<AssemblyCacheClaim> claims,
        AssemblyCacheRetention retention,
        DateTimeOffset nowUtc,
        int unrecognisedFiles = 0,
        long unrecognisedBytes = 0)
    {
        // A claim protects while it is fresh — and unconditionally when we could not read it.
        var claimed = claims
            .Where(c => c.AtUtc is not { } at || nowUtc - at <= retention.ClaimTtl)
            .GroupBy(c => c.Tag, StringComparer.OrdinalIgnoreCase)
            .ToImmutableDictionary(
                g => g.Key,
                g => string.Join(", ", g.Select(c => c.Holder)),
                StringComparer.OrdinalIgnoreCase);

        var recent = generations
            .OrderByDescending(g => g.NewestWriteUtc)
            .Take(Math.Max(0, retention.KeepGenerations))
            .Select(g => g.Tag)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

        var reasons = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        var collectable = ImmutableList.CreateBuilder<AssemblyCacheGeneration>();

        foreach (var generation in generations)
        {
            // Each branch is a KEEP. Ordered most-authoritative first purely so the reported reason
            // is the strongest one; any single match is enough to protect.
            var reason =
                string.Equals(generation.Tag, liveTag, StringComparison.OrdinalIgnoreCase)
                    ? "the framework this process is running"
                : claimed.TryGetValue(generation.Tag, out var holders)
                    ? $"claimed by {holders}"
                : recent.Contains(generation.Tag)
                    ? $"among the {retention.KeepGenerations} most recently written"
                : nowUtc - generation.NewestWriteUtc < retention.MinimumAge
                    ? $"newer than the {retention.MinimumAge} minimum age"
                : null;

            if (reason is null)
                collectable.Add(generation);
            else
                reasons[generation.Tag] = reason;
        }

        return new AssemblyCacheSweepPlan(
            liveTag, generations, claims, collectable.ToImmutable(), reasons.ToImmutable(),
            unrecognisedFiles, unrecognisedBytes);
    }

    /// <summary>
    /// Scan, plan, and — only when <see cref="AssemblyCacheRetention.Delete"/> is armed — collect.
    /// The filesystem work is a blocking leaf, so it runs on the I/O pool rather than on whatever
    /// scheduler subscribed.
    /// </summary>
    public static IObservable<AssemblyCacheSweepResult> Sweep(
        string rootDirectory,
        string liveTag,
        AssemblyCacheRetention retention,
        IIoPool pool,
        ILogger? logger = null)
        => pool.InvokeBlocking(_ =>
            SweepCore(rootDirectory, liveTag, retention, DateTimeOffset.UtcNow, logger));

    /// <summary>
    /// The sweep itself, with the clock passed in. Separated from <see cref="Sweep"/> so the
    /// behaviour that deletes files is exercised at an exact instant rather than against the wall
    /// clock.
    /// </summary>
    public static AssemblyCacheSweepResult SweepCore(
        string rootDirectory,
        string liveTag,
        AssemblyCacheRetention retention,
        DateTimeOffset nowUtc,
        ILogger? logger = null)
    {
        ImmutableList<AssemblyCacheGeneration> generations;
        ImmutableList<AssemblyCacheClaim> claims;
        int unrecognised;
        long unrecognisedBytes;
        try
        {
            (generations, unrecognised, unrecognisedBytes) = Scan(rootDirectory);
            claims = ReadClaims(rootDirectory);
        }
        catch (Exception ex)
        {
            // 🚨 FAIL CLOSED. Every other coordination path around the bake fails OPEN because the
            // cost of being wrong is duplicated work; here the cost of being wrong is deleting an
            // assembly a live pod is about to load. A partial listing is not a picture of the cache,
            // so it licenses nothing.
            logger?.LogWarning(ex,
                "AssemblyCacheRetention: could not read {Root} — NOTHING collected. A cache we cannot "
                + "enumerate completely can never be pruned safely",
                rootDirectory);
            return new AssemblyCacheSweepResult(null, false, 0, 0, 0, $"cache could not be read: {ex.Message}");
        }

        var plan = Plan(liveTag, generations, claims, retention, nowUtc, unrecognised, unrecognisedBytes);

        if (!retention.Delete)
        {
            logger?.LogInformation(
                "AssemblyCacheRetention: {Summary}. DELETING NOTHING — collection is not armed. "
                + "Keeping: {Kept}. Would collect: {Collectable}",
                plan.Summary,
                Describe(plan.Protected),
                plan.Collectable.Count == 0
                    ? "(nothing)"
                    : string.Join(", ", plan.Collectable.Select(g => $"{g.Tag} ({g.FileCount} files)")));
            return new AssemblyCacheSweepResult(plan, false, 0, 0, 0, null);
        }

        var deletedFiles = 0;
        var deletedBytes = 0L;
        var failed = 0;
        foreach (var generation in plan.Collectable)
            foreach (var path in generation.Files)
            {
                try
                {
                    var length = new FileInfo(path).Length;
                    File.Delete(path);
                    deletedFiles++;
                    deletedBytes += length;
                }
                catch (Exception ex)
                {
                    // Surfaced, not swallowed: the file stays, this sweep reports the failure, and the
                    // next sweep re-plans it. Aborting the whole sweep because one file is momentarily
                    // locked would leave the share growing for the sake of tidiness.
                    failed++;
                    logger?.LogWarning(ex,
                        "AssemblyCacheRetention: could not delete {Path} — it stays, and the next sweep "
                        + "will consider it again", path);
                }
            }

        logger?.LogInformation(
            "AssemblyCacheRetention: {Summary}. COLLECTED {DeletedFiles} file(s) across "
            + "{Generations} generation(s), reclaiming {DeletedMb:N1} MB{Failed}. Kept: {Kept}",
            plan.Summary, deletedFiles, plan.Collectable.Count, deletedBytes / (1024d * 1024d),
            failed == 0 ? "" : $" ({failed} file(s) could not be deleted)",
            Describe(plan.Protected));

        return new AssemblyCacheSweepResult(plan, true, deletedFiles, deletedBytes, failed, null);
    }

    private static string Describe(ImmutableDictionary<string, string> reasons) =>
        reasons.IsEmpty
            ? "(nothing)"
            : string.Join(" | ", reasons.Select(kvp => $"{kvp.Key} — {kvp.Value}"));

    /// <summary>
    /// Assert, and keep asserting, that this process is running framework <paramref name="tag"/>.
    /// This is the ONLY thing that proves a generation is still referenced, so it starts before any
    /// sweep can run and lives as long as the process does.
    ///
    /// <para>The interval is the assertion cadence, not a retry: a beat that fails is reported and the
    /// next one happens on schedule, because <see cref="AssemblyCacheRetention.ClaimTtl"/> is many
    /// multiples of it and one missed write must not read as "this generation is free".</para>
    /// </summary>
    public static IDisposable Claim(
        string rootDirectory,
        string tag,
        string holder,
        AssemblyCacheRetention retention,
        IIoPool pool,
        ILogger? logger = null)
        => Observable
            .Timer(TimeSpan.Zero, retention.ClaimRefreshInterval)
            .SelectMany(_ => pool
                .InvokeBlocking(_ => AssertClaim(rootDirectory, tag, holder))
                .Catch<Unit, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "AssemblyCacheRetention: could not assert the claim on framework {Tag} under "
                        + "{Root}. The generation stays protected by the recency and minimum-age "
                        + "rules; a claim that keeps failing past {Ttl} would stop protecting it",
                        tag, rootDirectory, retention.ClaimTtl);
                    return Observable.Return(Unit.Default);
                }))
            .Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex,
                    "AssemblyCacheRetention: the claim on framework {Tag} STOPPED — this pod no longer "
                    + "advertises the generation it is running", tag));

    /// <summary>
    /// Write one claim assertion. <c>internal</c> so a test drives the EXACT writer the heartbeat
    /// uses, rather than a hand-written approximation of the file format the sweep reads.
    /// </summary>
    internal static Unit AssertClaim(string rootDirectory, string tag, string holder)
    {
        var directory = Path.Combine(rootDirectory, ClaimsDirectoryName, tag.ToLowerInvariant());
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, Sanitize(holder)),
            $"{holder} {DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)}");
        return Unit.Default;
    }

    /// <summary>
    /// The instant a claim was last asserted, read from the file's CONTENT. Metadata would be the
    /// obvious place and is the wrong one: SMB metadata caching can report a last-write time that is
    /// stale when the holder is alive, and a falsely-stale claim is precisely the reading that
    /// deletes a live generation.
    /// </summary>
    internal static DateTimeOffset? ParseClaim(string content)
    {
        var separator = content.LastIndexOf(' ');
        if (separator < 0)
            return null;
        return DateTimeOffset.TryParse(
            content.AsSpan(separator + 1), CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out var at)
            ? at
            : null;
    }

    private static string Sanitize(string holder)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(holder.Length);
        foreach (var c in holder)
            sb.Append(invalid.Contains(c) ? '-' : c);
        return sb.Length == 0 ? "unknown" : sb.ToString();
    }
}
