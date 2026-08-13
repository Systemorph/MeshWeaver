#pragma warning disable CS1591

using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// Timeout-only diagnostics for the post-install read
/// (<c>GetMeshNodeStream(path).Where(n =&gt; n is not null).FirstAsync().Timeout(30s)</c>) that
/// issue #1405 keeps stalling.
///
/// <para>🚨 <b>Why this exists.</b> A 150-iteration loop of the two affected tests on <c>main</c>
/// reproduced the stall once, and the reproduction contained <b>no distinguishing log line at
/// all</b>: the failing window held strictly LESS than every passing window — thirty seconds of
/// pure silence between the install returning and the timeout. The two signals the issue's
/// diagnosis rests on are not discriminators:</para>
/// <list type="bullet">
///   <item><c>HubDisposingException: Hub Pack/Sibling is shutting down</c> (and its
///     <c>Dependent/Item</c> twin) fired in <b>51 of 51</b> runs — every pass included.</item>
///   <item>The <c>Own-node refresh … failed on Pack/Widget/Nested</c> /
///     <c>Cannot scan an assembly whose context is unloading</c> family appeared in <b>passing</b>
///     windows and NOT in the failing one.</item>
/// </list>
///
/// <para>So the next reproduction has to say something. These probes run ONLY after the read has
/// already timed out — never during the race, so they cannot perturb it (the same contract as
/// <c>WaitForLatestRelease</c>'s stuck-state diagnostic). They answer the one fork nobody can
/// currently resolve from a red run:</para>
/// <list type="number">
///   <item><b>Is the row durable?</b> If the mesh query finds it, the install DID write it and the
///     read path is what failed. If it does not, the stall is upstream of the read entirely and the
///     recycle framing is about the wrong stage.</item>
///   <item><b>Does a cache-BYPASSING read answer?</b> A fresh handle opens its own sync stream
///     instead of joining the shared per-path <c>IMeshNodeStreamCache</c> entry. If the bypass read
///     answers while the shared one never did, the orphaned thing is the CACHE ENTRY — it bound one
///     stream at creation and never rebinds. If neither answers, the owner is silent to everybody
///     and the cache is exonerated.</item>
/// </list>
///
/// <para>Every probe is bounded and swallows its own failure into text: a diagnostic that can hang
/// or throw would replace the failure it is explaining.</para>
/// </summary>
internal static class StalledReadDiagnostics
{
    /// <summary>Default per-probe bound. A diagnostic must never out-live the failure it explains.</summary>
    public static readonly TimeSpan DefaultProbeBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Wraps a post-install read so a stall reports WHERE it stalled instead of a bare
    /// <see cref="TimeoutException"/>.
    /// </summary>
    public static async Task<MeshNode> ReadOrExplain(
        IMessageHub mesh, string path, TimeSpan timeout, Action<string> log)
    {
        try
        {
            return await mesh.GetWorkspace().GetMeshNodeStream(path)
                .Where(n => n is not null).Select(n => n!)
                .FirstAsync().Timeout(timeout).ToTask();
        }
        catch (TimeoutException ex)
        {
            var report = await Describe(mesh, path);
            log(report);
            throw new TimeoutException(
                $"Post-install read of '{path}' produced no node within {timeout.TotalSeconds:0}s "
                + $"(issue #1405). {report}", ex);
        }
    }

    /// <summary>
    /// The report itself, exposed so its contract — resolves the fork, never throws, never
    /// out-runs its bound — is directly testable (<c>StalledReadDiagnosticsTest</c>).
    /// </summary>
    public static async Task<string> Describe(IMessageHub mesh, string path, TimeSpan? probeBudget = null)
    {
        var budget = probeBudget ?? DefaultProbeBudget;
        var durable = await Probe(() => DurableRow(mesh, path), budget);
        var bypass = await Probe(() => BypassCacheRead(mesh, path), budget);
        return $"durable row: {durable}; cache-bypassing read: {bypass}. "
               + "A durable row plus a working bypass read means the SHARED mesh-node cache entry "
               + "for this path is orphaned (bound once at creation, never rebound); a durable row "
               + "with both reads silent means the owner hub answers nobody; no durable row means "
               + "the install, not the read, is the stalled stage.";
    }

    private static async Task<string> Probe(Func<IObservable<string>> probe, TimeSpan budget)
    {
        try
        {
            return await probe()
                .Timeout(budget)
                .Catch((Exception ex) => Observable.Return($"probe failed: {ex.GetType().Name}: {ex.Message}"))
                .FirstAsync()
                .ToTask();
        }
        catch (Exception ex)
        {
            // Belt and braces: this text must ALWAYS be producible.
            return $"probe threw: {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static IObservable<string> DurableRow(IMessageHub mesh, string path) =>
        mesh.ServiceProvider.GetRequiredService<IMeshService>()
            .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{path}"))
            .Take(1)
            .Select(change =>
            {
                var node = change.Items.FirstOrDefault(n => n.Path == path);
                return node is null
                    ? "ABSENT (the query does not see it)"
                    : $"present (Version={node.Version}, NodeType='{node.NodeType ?? "(none)"}', State={node.State})";
            });

    // A handle opened with bypassCache:true builds its OWN sync stream to the owner rather than
    // joining the shared per-path cache entry — which is precisely the component suspected of
    // being orphaned, so it must not be the thing doing the asking.
    private static IObservable<string> BypassCacheRead(IMessageHub mesh, string path) =>
        mesh.GetWorkspace().GetMeshNodeStreamBypassCache(path)
            .Where(n => n is not null)
            .Take(1)
            .Select(n => $"answered (Version={n.Version}, NodeType='{n.NodeType ?? "(none)"}')");
}
