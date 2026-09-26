using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting;

/// <summary>
/// 🚨 <b>The durable record that a platform build has been ADMITTED to this mesh</b> (#5544). This
/// is the witness behind <see cref="NodeTypeBakeReport.ServedBefore"/>: a pod that finds its own
/// build here is a RESTART of an image that has served, not a candidate in a roll, so its bake gate
/// never refuses it.
///
/// <para><b>Why a marker, and not the NodeType records' provenance.</b> The records' own
/// <c>CompiledPlatformVersion</c> would have to name this build, and in the ordinary case it never
/// does. The compatibility key is equal across every build of one epoch, so an ordinary roll leaves
/// every type <c>Baked</c> and compiles nothing. Prebuilt adoption keeps the PRODUCER's platform
/// version on purpose. A serving image can therefore leave no record naming itself, and a restart
/// of it would read as a stranger. That was the review finding on the first cut of this fix.</para>
///
/// <para><b>Written only by an admitted process.</b> <see cref="Record"/> is offered to
/// <see cref="MeshPublicationGate"/>. It is held while the bake measures, written when the pod is
/// admitted, and discarded when the pod is refused. On a host that never armed the gate it is
/// written straight away, and such a host serves anyway. So a marker present means "a replica of
/// this build served traffic here". It never means "a replica of this build booted here".</para>
///
/// <para><b>A storage row, not a hub.</b> It is read and written through <see cref="IStorageAdapter"/>
/// directly, the same pattern as the build claim lock (<c>BuildNodeType.ClaimPath</c>). An absent
/// row is simply <c>false</c>. It is never a point read of a missing node through a hub, which would
/// answer a routing NotFound and trip the storm-breaker on the path.</para>
/// </summary>
public static class ServedBuildWitness
{
    /// <summary>The namespace the markers live under — one row per admitted platform build.</summary>
    public const string Namespace = "Admin/ServedPlatformBuilds";

    /// <summary>The marker's path for <paramref name="platformVersion"/>. Separators that a storage
    /// provider could read as path or extension syntax are replaced.</summary>
    /// <param name="platformVersion">The platform build, e.g. <c>3.0.0-ci.9218</c>.</param>
    public static string PathOf(string platformVersion) => $"{Namespace}/{IdOf(platformVersion)}";

    private static string IdOf(string platformVersion) =>
        platformVersion.Replace('.', '_').Replace('/', '_').Replace('+', '_');

    /// <summary>
    /// Whether a replica of <paramref name="platformVersion"/> has been admitted to this mesh:
    /// <c>true</c> when the row exists. It is <c>false</c> when the row is absent, when the build is
    /// unknown, when there is no storage, or when the read fails. Every one of those non-answers
    /// falls to the STRICT reading, so the gate is never relaxed on something it could not read.
    /// </summary>
    /// <param name="mesh">The mesh hub.</param>
    /// <param name="platformVersion">The running platform build, or <c>null</c> when unknown.</param>
    /// <param name="logger">Diagnostics.</param>
    public static IObservable<bool> HasServed(IMessageHub mesh, string? platformVersion, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(platformVersion))
            return Observable.Return(false);
        var storage = mesh.ServiceProvider.GetService<IStorageAdapter>();
        if (storage is null)
            return Observable.Return(false);
        var path = PathOf(platformVersion);
        return Observable.Defer(() => storage.Exists(path))
            .Take(1)
            .DefaultIfEmpty(false)
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex,
                    "ServedBuildWitness: could not read {Path} — treating build {Build} as NOT yet "
                    + "served here, which keeps the bake gate at its strict reading",
                    path, platformVersion);
                return Observable.Return(false);
            });
    }

    /// <summary>
    /// Sets <see cref="NodeTypeBakeReport.ServedBefore"/> on <paramref name="report"/> from the
    /// durable witness. This is the one place a gating report learns it.
    /// </summary>
    /// <param name="mesh">The mesh hub.</param>
    /// <param name="report">The probe report.</param>
    /// <param name="logger">Diagnostics.</param>
    public static IObservable<NodeTypeBakeReport> Annotate(
        IMessageHub mesh, NodeTypeBakeReport report, ILogger? logger)
        => HasServed(mesh, report.LivePlatformVersion, logger)
            .Select(served =>
            {
                if (served)
                    logger?.LogWarning(
                        "ServedBuildWitness: platform build {Build} has ALREADY been admitted to this "
                        + "mesh ({Path}), so this pod is a restart of a serving image, not a roll candidate. "
                        + "A bake failure here is REPORTED and does not refuse readiness (#5544).",
                        report.LivePlatformVersion, PathOf(report.LivePlatformVersion!));
                return report with { ServedBefore = served };
            });

    /// <summary>
    /// Records that this process's platform build has been admitted. The write goes through
    /// <see cref="MeshPublicationGate"/>, so it is HELD until the pod is admitted and DISCARDED if it
    /// is refused. Create-if-absent: only the first admitted replica of a build writes.
    /// </summary>
    /// <param name="mesh">The mesh hub.</param>
    /// <param name="platformVersion">This process's platform build, or <c>null</c> when unknown (then nothing is written).</param>
    /// <param name="logger">Diagnostics.</param>
    /// <returns>A cold observable; the caller subscribes.</returns>
    public static IObservable<Unit> Record(IMessageHub mesh, string? platformVersion, ILogger? logger)
    {
        if (string.IsNullOrWhiteSpace(platformVersion))
            return Observable.Return(Unit.Default);
        var storage = mesh.ServiceProvider.GetService<IStorageAdapter>();
        if (storage is null)
            return Observable.Return(Unit.Default);

        IObservable<Unit> Write() => Observable.Defer(() =>
        {
            var node = new MeshNode(IdOf(platformVersion), Namespace)
            {
                Name = $"Platform build {platformVersion} served here",
                LastModified = DateTimeOffset.UtcNow,
                Version = MeshNode.NextVersion(0),
            };
            return storage.WriteIfVersion(node, 0, mesh.JsonSerializerOptions)
                .Take(1)
                .Do(written =>
                {
                    if (written is true)
                        logger?.LogInformation(
                            "ServedBuildWitness: recorded that platform build {Build} is admitted to this mesh ({Path})",
                            platformVersion, node.Path);
                })
                .Select(_ => Unit.Default);
        });

        var gate = mesh.ServiceProvider.GetService<MeshPublicationGate>();
        return gate is null
            ? Write()
            : gate.Publish($"served-build witness for {platformVersion}", Write);
    }

    /// <summary>The running platform build, as the bake's reports know it.</summary>
    internal static string? LivePlatformVersion => NodeTypeCompilationHelpers.LivePlatformVersion;
}
