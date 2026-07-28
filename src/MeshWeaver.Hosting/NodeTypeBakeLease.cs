using System;
using System.Globalization;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// Where the fleet coordinates its bake — a directory every replica can see, normally the shared
/// assembly-cache volume. Registered by the host only when such a directory exists; absent it, there
/// is nothing to coordinate (a monolith, a test, a dev box) and every process simply bakes.
/// </summary>
/// <param name="LeaseDirectory">Shared directory the lease file lives in.</param>
public sealed record BakeCoordination(string LeaseDirectory);

/// <summary>
/// ONE POD BAKES. The others wait.
///
/// <para><b>Why this has to exist.</b> The compile cache is shared and durable, but the DECISION to
/// rebuild is per-process. With <c>maxSurge</c> during a rollout, or any <c>replicas &gt; 1</c>, every
/// pod on the new image independently discovers the same framework-stale cache and starts the same
/// sweep over the same NodeTypes into the same volume. That is not merely duplicated work: it is
/// several cold Roslyn compiles of the SAME type running concurrently, contending on one network
/// volume — the exact storm the sequential, dependency-ordered sweep exists to prevent. Four
/// concurrent compiles on memex (2026-07-28 04:05) dropped six plugin roots to the "did not settle"
/// overlay and needed a scale-to-zero.</para>
///
/// <para><b>How.</b> An atomic <c>CreateNew</c> on a lease file in a directory every replica can see.
/// The winner bakes and heartbeats; every other pod FOLLOWS — it polls the store probe and watches the
/// share fill, compiling nothing. The lease carries a heartbeat so a pod that dies mid-bake does not
/// wedge the fleet: once the timestamp goes stale any follower may steal it and finish the job.</para>
///
/// <para><b>Keyed per framework version.</b> A bake-ahead pod on a NEW image and the live pods on the
/// OLD one are baking different tags into different files and must not block each other; two replicas
/// of the SAME image must. The framework version is exactly that distinction, so it is the key.</para>
///
/// <para>Best-effort by construction: every failure path returns "you may bake". A coordination
/// mechanism that can deny work on an I/O error would turn a transient volume blip into a fleet that
/// never compiles anything — strictly worse than the duplicate work it prevents.</para>
/// </summary>
public sealed class NodeTypeBakeLease : IDisposable
{
    /// <summary>
    /// How long a lease survives without a heartbeat before a follower may steal it. Generously
    /// larger than <see cref="HeartbeatInterval"/>: a missed beat under load must never hand the bake
    /// to a second pod while the first is still working — that would produce the concurrent compile
    /// this class exists to prevent.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    /// <summary>How often the holder refreshes the lease while it bakes.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(2);

    private readonly string path;
    private readonly ILogger? logger;
    private readonly Timer? heartbeat;
    private int disposed;

    private NodeTypeBakeLease(string path, ILogger? logger)
    {
        this.path = path;
        this.logger = logger;
        heartbeat = new Timer(_ => Beat(), null, HeartbeatInterval, HeartbeatInterval);
    }

    /// <summary>The lease file for a framework version.</summary>
    public static string PathFor(string directory, string frameworkVersion) =>
        Path.Combine(directory, $".bake-lease-{Short(frameworkVersion)}");

    /// <summary>
    /// Try to become THE baker for <paramref name="frameworkVersion"/>. Returns the held lease, or
    /// <c>null</c> when another live pod holds it — in which case the caller must FOLLOW, not bake.
    ///
    /// <para>A lease whose heartbeat has gone stale is stolen (its holder died mid-bake), and any
    /// I/O failure resolves to "you may bake" so a coordination problem can never stop the fleet
    /// compiling.</para>
    /// </summary>
    public static NodeTypeBakeLease? TryAcquire(
        string directory, string frameworkVersion, string holder, ILogger? logger = null)
    {
        var path = PathFor(directory, frameworkVersion);
        try
        {
            Directory.CreateDirectory(directory);

            // Atomic: exactly one process across the fleet can create the file.
            try
            {
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream))
                    writer.Write(Stamp(holder));
                logger?.LogInformation(
                    "Bake lease ACQUIRED for framework {Framework} by {Holder} — this pod bakes; others follow",
                    Short(frameworkVersion), holder);
                return new NodeTypeBakeLease(path, logger);
            }
            catch (IOException)
            {
                // Someone holds it. Steal only if their heartbeat is stale.
            }

            var age = DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(path);
            if (age <= StaleAfter)
            {
                logger?.LogInformation(
                    "Bake lease for framework {Framework} is held elsewhere (last beat {Age} ago) — following, not baking",
                    Short(frameworkVersion), age);
                return null;
            }

            logger?.LogWarning(
                "Bake lease for framework {Framework} is STALE ({Age} since last heartbeat, limit {Limit}) — "
                + "its holder probably died mid-bake. Stealing it as {Holder}",
                Short(frameworkVersion), age, StaleAfter, holder);
            File.WriteAllText(path, Stamp(holder));
            return new NodeTypeBakeLease(path, logger);
        }
        catch (Exception ex)
        {
            // Fail OPEN: duplicate work is a cost, a fleet that never compiles is an outage.
            logger?.LogWarning(ex,
                "Bake lease could not be evaluated at {Path} — proceeding to bake without coordination",
                path);
            return new NodeTypeBakeLease(path, logger);
        }
    }

    private void Beat()
    {
        if (Volatile.Read(ref disposed) != 0)
            return;
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            // A missed beat only risks an early steal after StaleAfter — not worth failing the bake.
            logger?.LogDebug(ex, "Bake lease heartbeat failed at {Path}", path);
        }
    }

    private static string Stamp(string holder) =>
        $"{holder} {DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)}";

    private static string Short(string version) =>
        string.IsNullOrEmpty(version) ? "none" : version[..Math.Min(8, version.Length)];

    /// <summary>Releases the lease so the next image roll can acquire it immediately.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;

        heartbeat?.Dispose();
        try
        {
            File.Delete(path);
            logger?.LogInformation("Bake lease released at {Path}", path);
        }
        catch (Exception ex)
        {
            // Left behind, it simply expires after StaleAfter — never a permanent block.
            logger?.LogDebug(ex, "Bake lease could not be deleted at {Path} — it will expire", path);
        }
    }
}
