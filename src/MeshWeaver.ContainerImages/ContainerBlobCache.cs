using System.Reactive;
using System.Reactive.Linq;
using System.Security.Cryptography;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MeshWeaver.ContainerImages;

/// <summary>
/// One resident cache entry, opened for reading. The caller OWNS <see cref="Content"/> and must
/// dispose it — the handle is what keeps the bytes readable while eviction runs, because a sweep
/// that unlinks the file cannot pull it out from under an open reader.
/// </summary>
/// <param name="Content">The entry's bytes, positioned at zero.</param>
/// <param name="Length">The entry's length in bytes.</param>
/// <param name="MediaType">The media type the upstream served these bytes with, when one was
/// recorded. Essential for a manifest — a client parses by it — and absent for a layer.</param>
/// <param name="Digest">The content digest these bytes were verified against when stored.</param>
public sealed record ContainerCacheEntry(
    Stream Content, long Length, string? MediaType, string Digest);

/// <summary>
/// What one eviction sweep did. Returned so a caller can log it and a test can assert on it,
/// rather than inferring eviction from a directory listing.
/// </summary>
/// <param name="Ran">False when another sweep was already in flight and this one stood down.</param>
/// <param name="EntriesBefore">Entries resident when the sweep started.</param>
/// <param name="BytesBefore">Bytes resident when the sweep started.</param>
/// <param name="Evicted">Entries deleted.</param>
/// <param name="BytesEvicted">Bytes reclaimed.</param>
public sealed record ContainerCacheSweep(
    bool Ran, int EntriesBefore, long BytesBefore, int Evicted, long BytesEvicted);

/// <summary>
/// The read-through mirror's content-addressed cache: bytes the mirror has already fetched from
/// the upstream, stored under their DIGEST so a second pull is served without touching the
/// upstream at all.
///
/// <para><b>What is cached: digests, and only digests.</b> A blob route is a digest by
/// construction (<see cref="RegistryRoute"/> refuses anything else), and a manifest route is
/// cached only when the reference IS a digest. 🚨 A TAG is deliberately never cached — a tag is
/// mutable, so a cached tag would serve yesterday's image forever and the bug would look like a
/// stale build rather than a stale cache. Because every key is a content hash, an entry can never
/// go stale: there is no invalidation, no TTL, and no coherence protocol to get wrong. The cost of
/// that choice is stated plainly: a <c>pull repo:tag</c> always needs the upstream for the
/// tag → digest step, while a <c>pull repo@sha256:…</c> can be served entirely from cache.</para>
///
/// <para>🚨 <b>Every stored entry is VERIFIED against the digest it is filed under</b>, hashed
/// over the bytes as they stream past. A body that does not hash to its own name is served to the
/// caller and DISCARDED rather than stored, so the cache cannot be poisoned by a wrong answer
/// upstream and a resident entry is provably the bytes its digest names. That is what makes
/// serving one during an upstream outage safe.</para>
///
/// <para>🚨 <b>The cache is NOT an archive, and nothing may treat it as one.</b> It is bounded
/// (<see cref="ContainerImageOptions.CacheMaxBytes"/>) and evicts least-recently-used entries, so
/// a digest resident today may be gone tomorrow. Its guarantee is one-directional: a hit avoids
/// the upstream, a miss falls through to it. It can only ever ADD availability, never subtract
/// it — which is exactly why it is safe to ship before any retention policy exists, and exactly
/// why it must not be sold as protection against an upstream purge.</para>
///
/// <para>🚨 <b>Instance state only.</b> The eviction accounting below is an instance field on a
/// mesh-scoped singleton, never <c>static</c>: process-wide cache state survives mesh disposal and
/// bleeds across tests and tenants.</para>
/// </summary>
public sealed class ContainerBlobCache
{
    /// <summary>The only digest algorithm the cache stores under — the only one it can verify,
    /// and the only one any registry in the fleet emits. Anything else is not cacheable and falls
    /// through to the upstream rather than being stored unverified.</summary>
    public const string SupportedAlgorithm = "sha256";

    /// <summary>Extension of the sidecar holding an entry's media type.</summary>
    internal const string TypeSuffix = ".type";

    /// <summary>Extension of an in-progress fill. Never readable as an entry.</summary>
    internal const string TemporarySuffix = ".tmp";

    /// <summary>A temporary file older than this is crash residue and is swept.</summary>
    private static readonly TimeSpan TemporaryFileLifetime = TimeSpan.FromHours(1);

    private readonly ContainerImageOptions options;
    private readonly ILogger<ContainerBlobCache> logger;
    private readonly IIoPool fileSystem;
    private readonly string? root;
    private readonly long sweepThreshold;

    // 🚨 Instance fields, never static. `bytesSinceSweep` starts AT the threshold so the very
    // first store sweeps: without that, a process that restarts more often than it stores an
    // eighth of the budget would never sweep at all and the directory would grow forever.
    private long bytesSinceSweep;
    private int sweepInFlight;

    /// <summary>Creates the cache.</summary>
    /// <param name="options">The mirror's configuration; <c>CacheDirectory</c> decides whether the
    /// cache is on at all.</param>
    /// <param name="services">Resolves the mesh-scoped I/O pool the cache bounds its disk work
    /// with.</param>
    /// <param name="logger">Reports evictions, refused fills and degraded reads.</param>
    public ContainerBlobCache(
        IOptions<ContainerImageOptions> options,
        IServiceProvider services,
        ILogger<ContainerBlobCache> logger)
    {
        this.options = options.Value;
        this.logger = logger;
        fileSystem = ContainerImagePools.Resolve(services, IoPoolNames.FileSystem);
        root = string.IsNullOrWhiteSpace(this.options.CacheDirectory)
            ? null
            : Path.GetFullPath(this.options.CacheDirectory);
        sweepThreshold = Math.Max(1024 * 1024, this.options.CacheMaxBytes / 8);
        bytesSinceSweep = sweepThreshold;
    }

    /// <summary>True when a cache directory is configured. False means the mirror proxies every
    /// pull, which is a supported mode and not a degraded one.</summary>
    public bool IsEnabled => root is not null;

    /// <summary>The resolved cache directory, or null when the cache is off.</summary>
    public string? Directory => root;

    /// <summary>
    /// Whether <paramref name="reference"/> is a digest this cache can store — the shape
    /// <c>sha256:&lt;64 lowercase hex&gt;</c>. Anything else (a tag, another algorithm) is not
    /// cacheable and is proxied every time.
    /// </summary>
    /// <param name="reference">A manifest or blob reference.</param>
    /// <returns>True when the reference is a storable digest.</returns>
    public static bool IsSupportedDigest(string? reference)
    {
        if (reference is null)
            return false;
        var colon = reference.IndexOf(':');
        if (colon != SupportedAlgorithm.Length
            || !reference.AsSpan(0, colon).SequenceEqual(SupportedAlgorithm))
            return false;
        var hex = reference.AsSpan(colon + 1);
        if (hex.Length != 64)
            return false;
        foreach (var c in hex)
            if (!char.IsAsciiDigit(c) && c is not (>= 'a' and <= 'f'))
                return false;
        return true;
    }

    /// <summary>
    /// Opens the entry for <paramref name="digest"/>, or emits null on a miss.
    ///
    /// <para>🚨 A disk error is a MISS, not a failure. The upstream is the source of truth, so
    /// falling through to it is the correct answer to "the cache could not be read" — the fault is
    /// reported at warning level rather than swallowed, and the caller never sees an error it
    /// could not act on.</para>
    ///
    /// <para>Cold: nothing is opened until subscribed.</para>
    /// </summary>
    /// <param name="digest">The content digest to look up.</param>
    /// <returns>The open entry, or null when it is not resident.</returns>
    public IObservable<ContainerCacheEntry?> Open(string digest)
    {
        if (!TryPaths(digest, out var blobPath, out var typePath))
            return Observable.Return<ContainerCacheEntry?>(null);

        return fileSystem.InvokeBlocking<ContainerCacheEntry?>(_ =>
        {
            // 🚨 Declared OUTSIDE the try so every failure path can dispose it. Ownership of this
            // handle transfers to the caller ONLY on the successful return; until then it is ours,
            // and the sidecar read below can still throw. A leaked descriptor here would be
            // invisible — the pull falls through to the upstream and looks entirely healthy —
            // while the process quietly walks toward its file-descriptor limit, one degraded cache
            // read at a time.
            FileStream? content = null;
            try
            {
                // FileShare.Delete so a concurrent eviction can unlink the file while this handle
                // holds it open: the reader keeps reading the bytes it already opened, and the
                // name simply stops resolving for the next caller.
                content = new FileStream(blobPath, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read | FileShare.Delete,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                });
                var mediaType = File.Exists(typePath) ? File.ReadAllText(typePath).Trim() : null;
                var entry = new ContainerCacheEntry(
                    content, content.Length,
                    string.IsNullOrEmpty(mediaType) ? null : mediaType, digest);
                content = null; // handed to the caller, who disposes it
                return entry;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex,
                    "Container registry mirror: the cache entry for {Digest} could not be read, "
                    + "so this pull falls through to the upstream. Check {Directory}.",
                    digest, root);
                return null;
            }
            finally
            {
                // Non-null only when we did NOT return the entry — a sidecar read that threw after
                // the blob opened, or any other unwind.
                content?.Dispose();
            }
        });
    }

    /// <summary>
    /// Marks <paramref name="digest"/> as just used, so eviction sees a genuine LEAST-RECENTLY-USED
    /// order rather than a least-recently-added one. The last-write timestamp IS the access
    /// timestamp here — file access times are unreliable (a <c>noatime</c> mount records none), so
    /// the cache maintains its own.
    ///
    /// <para>Cold, and meant to be subscribed OFF the response path: a touch that fails must never
    /// affect a pull that has already been answered correctly.</para>
    /// </summary>
    /// <param name="digest">The digest that was just served from cache.</param>
    /// <returns>A single unit when the timestamp has been updated.</returns>
    public IObservable<Unit> Touch(string digest)
    {
        if (!TryPaths(digest, out var blobPath, out _))
            return Observable.Return(Unit.Default);

        return fileSystem.InvokeBlocking(_ =>
        {
            if (File.Exists(blobPath))
                File.SetLastWriteTimeUtc(blobPath, DateTime.UtcNow);
            return Unit.Default;
        });
    }

    /// <summary>
    /// Begins a streaming fill for <paramref name="digest"/>: a write-only stream that forwards
    /// every byte to <paramref name="downstream"/> while hashing it and writing it to a temporary
    /// file, committed only if the hash matches.
    ///
    /// <para>Returns null when the cache is off or the digest is not storable — the caller then
    /// streams straight to the response, which is the pre-cache behaviour exactly.</para>
    ///
    /// <para>🚨 No I/O happens here. The temporary file is opened on the FIRST WRITE, inside the
    /// caller's pool slot, so a response that never produces a body (a HEAD, an error) leaves
    /// nothing behind and nothing is opened outside the bound that governs the transfer.</para>
    /// </summary>
    /// <param name="digest">The digest the bytes must hash to.</param>
    /// <param name="mediaType">The media type to record alongside, when the upstream declared
    /// one.</param>
    /// <param name="downstream">The response body every byte is also written to.</param>
    /// <returns>The fill, or null when this response is not cacheable.</returns>
    public ContainerBlobFill? BeginFill(string digest, string? mediaType, Stream downstream)
        => TryPaths(digest, out var blobPath, out var typePath)
            ? new ContainerBlobFill(this, digest, mediaType, blobPath, typePath, downstream, logger)
            : null;

    /// <summary>
    /// Stores bytes already held in memory — a manifest, which is kilobytes and was read whole in
    /// order to record its closure. Verified against <paramref name="digest"/> exactly as a
    /// streamed fill is.
    ///
    /// <para>Cold, and meant to be subscribed OFF the response path with an explicit error arm:
    /// storing is an optimisation and must never fail a pull.</para>
    /// </summary>
    /// <param name="digest">The digest the bytes must hash to.</param>
    /// <param name="mediaType">The media type the upstream served them with.</param>
    /// <param name="bytes">The body exactly as served.</param>
    /// <returns>True when the entry was stored, false when it was refused (hash mismatch, or the
    /// cache is off).</returns>
    public IObservable<bool> Store(string digest, string? mediaType, byte[] bytes)
    {
        if (!TryPaths(digest, out var blobPath, out var typePath))
            return Observable.Return(false);

        return fileSystem.InvokeBlocking(_ =>
        {
            var actual = Format(SHA256.HashData(bytes));
            if (!string.Equals(actual, digest, StringComparison.Ordinal))
            {
                LogDigestMismatch(digest, actual);
                return false;
            }
            var temporary = blobPath + "." + Guid.NewGuid().ToString("N") + TemporarySuffix;
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(blobPath)!);
            File.WriteAllBytes(temporary, bytes);
            return Publish(temporary, blobPath, typePath, mediaType, bytes.LongLength);
        });
    }

    /// <summary>
    /// Runs one eviction sweep: deletes crash-residue temporaries and orphaned sidecars, then — if
    /// the directory is over <see cref="ContainerImageOptions.CacheMaxBytes"/> — evicts
    /// least-recently-used entries until it is back under 90 % of the budget.
    ///
    /// <para>🚨 An EXPLICIT sweep always sweeps. Only the automatic one behind a store stands down
    /// when another is already running — because that one is opportunistic, while a caller who
    /// asked for a sweep is entitled to have one happen. A sweep that quietly did nothing would be
    /// indistinguishable from one that found nothing, which is the same shape as a CI gate that
    /// passes on skipped input.</para>
    ///
    /// <para>Concurrent sweeps are safe: deletion is idempotent and each enumeration is its own
    /// snapshot. Cold.</para>
    /// </summary>
    /// <returns>What the sweep did.</returns>
    public IObservable<ContainerCacheSweep> Sweep()
        => root is null
            ? Observable.Return(new ContainerCacheSweep(false, 0, 0, 0, 0))
            : fileSystem.InvokeBlocking(_ => SweepCore(root, standDownIfBusy: false));

    /// <summary>
    /// Accounts for a committed entry and triggers a sweep once roughly an eighth of the budget
    /// has been added since the last one.
    ///
    /// <para>The true resident total is measured only INSIDE a sweep, so nothing has to be
    /// hydrated at startup and nothing drifts in a way that matters: between sweeps the cache
    /// overshoots the budget by at most that eighth, which is what a budget with hysteresis is
    /// supposed to do.</para>
    /// </summary>
    internal void OnStored(long bytes)
    {
        if (Interlocked.Add(ref bytesSinceSweep, Math.Max(0, bytes)) < sweepThreshold)
            return;
        Interlocked.Exchange(ref bytesSinceSweep, 0);
        // Opportunistic, so it stands down behind one already running — unlike the explicit
        // Sweep() above, which a caller is entitled to have actually happen.
        fileSystem.InvokeBlocking(_ => SweepCore(root!, standDownIfBusy: true)).Subscribe(
            report =>
            {
                if (report is { Ran: true, Evicted: > 0 })
                    logger.LogInformation(
                        "Container registry mirror: evicted {Evicted} cache entr(ies), {Bytes:N0} "
                        + "bytes, from {Directory} — it held {BytesBefore:N0} of a {Budget:N0} "
                        + "byte budget.",
                        report.Evicted, report.BytesEvicted, root, report.BytesBefore,
                        options.CacheMaxBytes);
            },
            ex => logger.LogWarning(ex,
                "Container registry mirror: the cache eviction sweep over {Directory} failed. "
                + "Pulls are unaffected; the directory may exceed its {Budget:N0} byte budget "
                + "until the next sweep.", root, options.CacheMaxBytes));
    }

    /// <summary>
    /// Moves a completed temporary into place. The SIDECAR is written first: a reader that finds
    /// the blob must be able to find its media type, and the blob's arrival is what makes the
    /// entry visible.
    /// </summary>
    internal bool Publish(
        string temporary, string blobPath, string typePath, string? mediaType, long size)
    {
        try
        {
            if (!string.IsNullOrEmpty(mediaType))
                File.WriteAllText(typePath, mediaType);
            // overwrite: false — a concurrent fill of the same digest may have won the race. Its
            // bytes are ours by definition (both were verified against the same hash), so losing
            // the race is a success, not a conflict.
            File.Move(temporary, blobPath, overwrite: false);
        }
        catch (IOException) when (File.Exists(blobPath))
        {
            Delete(temporary);
            return true;
        }
        OnStored(size);
        return true;
    }

    /// <summary>Deletes a path, tolerating one that is already gone.</summary>
    internal static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file another process holds open. The next sweep will pick it up; there is nothing
            // useful to do here and nothing that a pull depends on.
        }
    }

    /// <summary>Reports a body that did not hash to the digest it was requested under.</summary>
    internal void LogDigestMismatch(string requested, string actual) =>
        logger.LogWarning(
            "Container registry mirror: the body served for {Requested} hashes to {Actual}, so it "
            + "was passed to the caller but NOT stored. A cache entry is only ever the bytes its "
            + "digest names.", requested, actual);

    /// <summary>The canonical spelling of a digest over raw hash bytes.</summary>
    internal static string Format(byte[] hash) =>
        SupportedAlgorithm + ":" + Convert.ToHexString(hash).ToLowerInvariant();

    /// <summary>
    /// The on-disk paths for a digest, or false when the cache is off or the digest is not
    /// storable. Sharded on the first two hex characters so one directory never holds a hundred
    /// thousand entries.
    /// </summary>
    private bool TryPaths(string digest, out string blobPath, out string typePath)
    {
        blobPath = string.Empty;
        typePath = string.Empty;
        if (root is null || !IsSupportedDigest(digest))
            return false;
        var hex = digest[(SupportedAlgorithm.Length + 1)..];
        blobPath = Path.Combine(root, SupportedAlgorithm, hex[..2], hex);
        typePath = blobPath + TypeSuffix;
        return true;
    }

    private ContainerCacheSweep SweepCore(string directory, bool standDownIfBusy)
    {
        // Not a gate and not a wait — nothing ever parks here. The flag only lets the OPPORTUNISTIC
        // sweep behind a store skip work another sweep is already doing; two concurrent sweeps are
        // harmless (deletion is idempotent, each enumeration is its own snapshot), so an explicit
        // sweep never stands down.
        var idle = Interlocked.CompareExchange(ref sweepInFlight, 1, 0) == 0;
        if (!idle && standDownIfBusy)
            return new ContainerCacheSweep(false, 0, 0, 0, 0);
        try
        {
            if (!System.IO.Directory.Exists(directory))
                return new ContainerCacheSweep(true, 0, 0, 0, 0);

            var entries = new List<(string Path, long Size, DateTime Touched)>();
            var total = 0L;
            var cutoff = DateTime.UtcNow - TemporaryFileLifetime;

            foreach (var path in System.IO.Directory.EnumerateFiles(
                         directory, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(path);
                if (path.EndsWith(TemporarySuffix, StringComparison.Ordinal))
                {
                    // Crash residue: a fill that never committed. Anything younger may still be
                    // in flight.
                    if (info.LastWriteTimeUtc < cutoff)
                        Delete(path);
                    continue;
                }
                if (path.EndsWith(TypeSuffix, StringComparison.Ordinal))
                {
                    // An orphan sidecar — its blob was evicted, or a fill failed between the two
                    // writes. Harmless, but it would otherwise accumulate forever.
                    if (!File.Exists(path[..^TypeSuffix.Length]))
                        Delete(path);
                    continue;
                }
                entries.Add((path, info.Length, info.LastWriteTimeUtc));
                total += info.Length;
            }

            var before = total;
            var evicted = 0;
            var reclaimed = 0L;
            if (total > options.CacheMaxBytes)
            {
                // 90 % rather than exactly the budget, so the next store does not immediately
                // trip another sweep.
                var target = (long)(options.CacheMaxBytes * 0.9);
                foreach (var entry in entries.OrderBy(e => e.Touched))
                {
                    if (total <= target)
                        break;
                    Delete(entry.Path);
                    Delete(entry.Path + TypeSuffix);
                    total -= entry.Size;
                    reclaimed += entry.Size;
                    evicted++;
                }
            }

            return new ContainerCacheSweep(true, entries.Count, before, evicted, reclaimed);
        }
        finally
        {
            if (idle)
                Interlocked.Exchange(ref sweepInFlight, 0);
        }
    }
}
