using System;
using System.Globalization;
using System.IO;
using System.Threading;
using MeshWeaver.Mesh.Services;
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
/// share fill, compiling nothing.</para>
///
/// <para><b>🚨 Takeover is decided by CLUSTER MEMBERSHIP, not by a clock.</b> The holder stamps its
/// <see cref="IClusterMembership.LocalIdentity"/> into the lease, so the question "did the baker
/// die?" is answered by the thing that actually knows — Orleans membership, which already runs
/// probes, indirect probes and a membership table for exactly this. That makes takeover
/// level-triggered on the truth:
/// <list type="bullet">
/// <item><description><b>Membership says the holder is GONE</b> → take it over immediately. There is
/// no staleness budget to wait out, so a pod that dies mid-bake costs the fleet the poll interval
/// rather than <see cref="StaleAfter"/>.</description></item>
/// <item><description><b>Membership says the holder is ALIVE</b> → never take it, however old the
/// heartbeat looks. This is also what makes an SMB metadata read unable to cause a double bake: the
/// timestamp is no longer the evidence.</description></item>
/// <item><description><b>Membership has no opinion</b> — no cluster at all (monolith, test, dev), a
/// pre-membership lease with no identity stamped, or a silo the snapshot does not list — → fall back
/// to the heartbeat clock below. That fallback is the ONLY thing the clock is still for.</description></item>
/// </list></para>
///
/// <para><b>The heartbeat is a WRITE, not a touch.</b> The instant lives in the lease file's CONTENT.
/// <c>SetLastWriteTimeUtc</c> would put it in metadata, which Azure Files may serve from cache — and
/// a falsely-stale metadata read is precisely the misreading that puts two pods on one compile.</para>
///
/// <para><b>Keyed per framework version.</b> A bake-ahead pod on a NEW image and the live pods on the
/// OLD one are baking different tags into different files and must not block each other; two replicas
/// of the SAME image must. The framework version is exactly that distinction, so it is the key.</para>
///
/// <para><b>🚨 Where it fails open, and where it no longer does.</b> Failing open used to be blanket:
/// every error path returned "you may bake", which is where "single silo" stopped being a guarantee.
/// It is now split by what the failure actually tells us:
/// <list type="bullet">
/// <item><description><b>No coordination SUBSTRATE</b> (the shared directory cannot be created or
/// written, the lease path is not a usable file) → fail OPEN and bake. There is no fleet to
/// coordinate with; a coordination mechanism that could deny work here would turn a volume blip into
/// a fleet that never compiles.</description></item>
/// <item><description><b>The substrate works but the holder is INDETERMINATE</b> (the lease cannot
/// be read or parsed, the takeover write fails) → FOLLOW, do not bake. Following is not "never
/// compile": the follower re-probes and re-attempts the lease every
/// <c>DynamicTypePreWarmer.FollowPollInterval</c>, so being wrong here costs one poll, whereas baking
/// costs the concurrent-compile storm this class exists to prevent.</description></item>
/// </list></para>
/// </summary>
public sealed class NodeTypeBakeLease : IDisposable
{
    /// <summary>
    /// How long a lease survives without a heartbeat before a follower may take it over — used ONLY
    /// when cluster membership has no opinion about the holder (no cluster, or an unresolvable
    /// identity). Generously larger than <see cref="HeartbeatInterval"/>: on that path a missed beat
    /// under load must never hand the bake to a second pod while the first is still working.
    ///
    /// <para>Where membership DOES answer, this budget is not consulted at all — neither to wait it
    /// out for a dead holder nor to expire a live one.</para>
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    /// <summary>How often the holder refreshes the lease while it bakes.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(2);

    private readonly string path;
    private readonly string holder;
    private readonly string? identity;
    private readonly ILogger? logger;
    private readonly Timer? heartbeat;
    private int disposed;

    private NodeTypeBakeLease(string path, string holder, string? identity, ILogger? logger)
    {
        this.path = path;
        this.holder = holder;
        this.identity = identity;
        this.logger = logger;
        heartbeat = new Timer(_ => Beat(), null, HeartbeatInterval, HeartbeatInterval);
    }

    /// <summary>The lease file for a framework version.</summary>
    public static string PathFor(string directory, string frameworkVersion) =>
        Path.Combine(directory, $".bake-lease-{Short(frameworkVersion)}");

    /// <summary>
    /// Try to become THE baker for <paramref name="frameworkVersion"/>. Returns the held lease, or
    /// <c>null</c> when the caller must FOLLOW rather than bake.
    ///
    /// <para>See the class remarks for the decision order: cluster membership first (the fact), the
    /// heartbeat clock only where membership has no opinion, and a split fail-open/follow rule that
    /// distinguishes a broken substrate from an unknown holder.</para>
    /// </summary>
    /// <param name="directory">Shared directory every replica can see.</param>
    /// <param name="frameworkVersion">The framework identity this bake targets — the lease key.</param>
    /// <param name="holder">Human-readable holder (the pod / machine name), for the logs.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="membership">
    /// Cluster membership, when this host is part of a cluster. <c>null</c> — a monolith, a test, an
    /// Orleans client — means every holder resolves to <see cref="ClusterMemberState.Unknown"/> and
    /// the heartbeat clock decides, which is the pre-membership behaviour.
    /// </param>
    public static NodeTypeBakeLease? TryAcquire(
        string directory,
        string frameworkVersion,
        string holder,
        ILogger? logger = null,
        IClusterMembership? membership = null)
    {
        var path = PathFor(directory, frameworkVersion);
        var identity = SafeLocalIdentity(membership, logger);

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            // No substrate — see the class remarks. Bake.
            logger?.LogWarning(ex,
                "Bake lease directory {Directory} is unusable — proceeding to bake without coordination",
                directory);
            return new NodeTypeBakeLease(path, holder, identity, logger);
        }

        switch (TryCreate(path, holder, identity, logger, out var acquired))
        {
            case CreateOutcome.Acquired:
                logger?.LogInformation(
                    "Bake lease ACQUIRED for framework {Framework} by {Holder} — this pod bakes; others follow",
                    Short(frameworkVersion), holder);
                return acquired;

            case CreateOutcome.Broken:
                logger?.LogWarning(
                    "Bake lease at {Path} could not be created and is not held by anyone — proceeding "
                    + "to bake without coordination", path);
                return new NodeTypeBakeLease(path, holder, identity, logger);
        }

        // Someone holds it. WHO, and are they still running?
        var stamp = TryReadStamp(path, out var vanished);

        if (vanished)
        {
            // The holder RELEASED between our create attempt and our read. That is an observed state
            // transition, not a transient — and the response to "the lease is free" is to take it.
            // One attempt, because if it fails the lease is held again and following is correct.
            if (TryCreate(path, holder, identity, logger, out var reacquired) == CreateOutcome.Acquired)
            {
                logger?.LogInformation(
                    "Bake lease for framework {Framework} was released as we read it — ACQUIRED by "
                    + "{Holder}", Short(frameworkVersion), holder);
                return reacquired;
            }
            logger?.LogInformation(
                "Bake lease for framework {Framework} was released and immediately re-taken by another "
                + "pod — following, not baking", Short(frameworkVersion));
            return null;
        }

        var state = stamp?.Identity is { } holderIdentity && membership is not null
            ? SafeStateOf(membership, holderIdentity, logger)
            : ClusterMemberState.Unknown;

        switch (state)
        {
            case ClusterMemberState.Alive:
                logger?.LogInformation(
                    "Bake lease for framework {Framework} is held by {Holder}, which cluster membership "
                    + "reports LIVE — following, not baking. The heartbeat is not consulted: membership "
                    + "is the fact, and a cached-stale file timestamp must not be able to put two pods "
                    + "on one compile",
                    Short(frameworkVersion), stamp!.Value.Holder);
                return null;

            case ClusterMemberState.Gone:
                logger?.LogWarning(
                    "Bake lease for framework {Framework} is held by {Holder}, which cluster membership "
                    + "reports is NO LONGER A MEMBER — taking it over as {NewHolder} immediately, with "
                    + "no staleness budget to wait out",
                    Short(frameworkVersion), stamp!.Value.Holder, holder);
                return TakeOver(path, holder, identity, logger);
        }

        // Membership has no opinion — no cluster, or a holder it cannot resolve. The clock decides.
        var beat = stamp?.At ?? LastWriteUtc(path);
        if (beat is null)
        {
            // The substrate works, but we cannot tell anything about the holder. FOLLOW: being wrong
            // costs one poll, and baking costs the storm.
            logger?.LogWarning(
                "Bake lease for framework {Framework} at {Path} is held but its holder cannot be "
                + "determined (unreadable stamp, no cluster membership) — following, not baking. The "
                + "next poll re-evaluates it",
                Short(frameworkVersion), path);
            return null;
        }

        var age = DateTimeOffset.UtcNow - beat.Value;
        if (age <= StaleAfter)
        {
            logger?.LogInformation(
                "Bake lease for framework {Framework} is held by {Holder} (last beat {Age} ago, no "
                + "cluster membership to ask) — following, not baking",
                Short(frameworkVersion), stamp?.Holder ?? "(unknown)", age);
            return null;
        }

        logger?.LogWarning(
            "Bake lease for framework {Framework} is STALE ({Age} since last heartbeat, limit {Limit}) "
            + "and no cluster membership can say whether {Holder} is alive — its holder probably died "
            + "mid-bake. Taking it over as {NewHolder}",
            Short(frameworkVersion), age, StaleAfter, stamp?.Holder ?? "(unknown)", holder);
        return TakeOver(path, holder, identity, logger);
    }

    private enum CreateOutcome
    {
        /// <summary>This process created the lease file — it is THE baker.</summary>
        Acquired,
        /// <summary>The file already exists, so someone else holds it.</summary>
        AlreadyHeld,
        /// <summary>The file does not exist and could not be created — no usable lease path.</summary>
        Broken
    }

    /// <summary>
    /// The atomic acquisition: exactly one process across the fleet can create the file. An
    /// <see cref="IOException"/> is ambiguous on its own — it covers both "already exists" and a real
    /// I/O failure — so the two are separated by asking whether the file is actually there. That
    /// distinction is what lets a broken path fail OPEN while a genuinely-held lease does not.
    /// </summary>
    private static CreateOutcome TryCreate(
        string path, string holder, string? identity, ILogger? logger, out NodeTypeBakeLease? lease)
    {
        lease = null;
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
                writer.Write(Stamp(holder, identity));
        }
        catch (Exception)
        {
            return File.Exists(path) ? CreateOutcome.AlreadyHeld : CreateOutcome.Broken;
        }
        lease = new NodeTypeBakeLease(path, holder, identity, logger);
        return CreateOutcome.Acquired;
    }

    private static NodeTypeBakeLease? TakeOver(
        string path, string holder, string? identity, ILogger? logger)
    {
        try
        {
            File.WriteAllText(path, Stamp(holder, identity));
            return new NodeTypeBakeLease(path, holder, identity, logger);
        }
        catch (Exception ex)
        {
            // We decided the lease was takeable and could not take it. FOLLOW rather than bake: the
            // decision was ours, the failure is the share's, and a second baker is the worse outcome.
            logger?.LogWarning(ex,
                "Bake lease at {Path} could be taken over but the write failed — following, not "
                + "baking. The next poll re-attempts it", path);
            return null;
        }
    }

    /// <summary>
    /// The heartbeat: rewrite the stamp, so the instant lands in the file's CONTENT. A metadata touch
    /// would be cheaper and is what this used to do — and on Azure Files a cached metadata read is
    /// exactly how a live holder's lease looks stale.
    /// </summary>
    private void Beat()
    {
        if (Volatile.Read(ref disposed) != 0)
            return;
        try
        {
            File.WriteAllText(path, Stamp(holder, identity));
        }
        catch (Exception ex)
        {
            // A missed beat only matters on the no-membership fallback path, and only after
            // StaleAfter — not worth failing the bake over.
            logger?.LogDebug(ex, "Bake lease heartbeat failed at {Path}", path);
        }
    }

    /// <summary>
    /// <c>{holder} {identity} {instant}</c>. The identity is <c>-</c> when this host has no cluster,
    /// keeping the shape fixed at three fields; a two-field stamp is a pre-membership lease and parses
    /// with no identity, which resolves to <see cref="ClusterMemberState.Unknown"/> and the clock.
    /// </summary>
    private static string Stamp(string holder, string? identity) =>
        $"{holder} {(string.IsNullOrWhiteSpace(identity) ? "-" : identity)} "
        + DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

    private readonly record struct LeaseStamp(string Holder, string? Identity, DateTimeOffset? At);

    /// <summary>
    /// Read the holder's stamp. <paramref name="vanished"/> distinguishes "the lease was released
    /// under us" — which means acquire — from "it is there but I cannot make sense of it" — which
    /// means follow.
    ///
    /// <para>A partial read is possible in principle, because the heartbeat truncates and rewrites in
    /// place. It degrades safely: an unparseable instant falls back to the file time, and an
    /// unparseable identity falls back to the clock — both strictly more conservative than the truth.</para>
    /// </summary>
    private static LeaseStamp? TryReadStamp(string path, out bool vanished)
    {
        vanished = false;
        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            vanished = true;
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            vanished = true;
            return null;
        }
        catch (Exception)
        {
            return null;
        }

        var parts = content.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            // {holder} {identity} {instant}
            >= 3 => new LeaseStamp(parts[0], parts[1] == "-" ? null : parts[1], ParseInstant(parts[2])),
            // Pre-membership: {holder} {instant}
            2 => new LeaseStamp(parts[0], null, ParseInstant(parts[1])),
            1 => new LeaseStamp(parts[0], null, null),
            _ => null
        };
    }

    private static DateTimeOffset? ParseInstant(string value) =>
        DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? at
            : null;

    private static DateTimeOffset? LastWriteUtc(string path)
    {
        try
        {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? SafeLocalIdentity(IClusterMembership? membership, ILogger? logger)
    {
        if (membership is null)
            return null;
        try
        {
            return membership.LocalIdentity;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Cluster membership could not report this host's identity");
            return null;
        }
    }

    private static ClusterMemberState SafeStateOf(
        IClusterMembership membership, string identity, ILogger? logger)
    {
        try
        {
            return membership.StateOf(identity);
        }
        catch (Exception ex)
        {
            // A membership service that throws has told us nothing — and "nothing" must never read
            // as "gone".
            logger?.LogDebug(ex, "Cluster membership threw resolving {Identity}", identity);
            return ClusterMemberState.Unknown;
        }
    }

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
            // Left behind, it is taken over as soon as membership reports the holder gone — or, with
            // no membership, after StaleAfter. Never a permanent block.
            logger?.LogDebug(ex, "Bake lease could not be deleted at {Path} — it will be taken over", path);
        }
    }
}
