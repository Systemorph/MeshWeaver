using System.Reactive.Linq;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI.Copilot;

/// <summary>
/// The GitHub Copilot model list, exposed as a <b>live reactive stream</b> backed by
/// the workspace's shared synced-query cache (<c>workspace.GetQuery(...)</c>) — NOT a
/// one-shot CLI probe cached in a field.
///
/// <para><b>Why GetQuery, not a pooled load.</b> "We want to know this" = we want the
/// current set of Copilot models, kept current. That is a <i>read of mesh state</i>, and
/// the canonical primitive for "a set, live" is the synced query: <c>GetQuery</c> returns
/// the cached <c>IObservable&lt;IEnumerable&lt;MeshNode&gt;&gt;</c> for the query, emits the
/// initial set plus a delta on every change, and is shared process-wide (one upstream
/// subscription regardless of how many pickers/tabs bind it). The GUI data-binds this
/// stream directly; it is never a snapshot. (See <c>CqrsAndContentAccess.md</c> →
/// "Sets / listings, live" and <c>AgentChatClient.Initialize</c> for the canonical shape.)</para>
///
/// <para><b>AccessContext.</b> <c>GetQuery</c> opens its upstream <c>SubscribeRequest</c>
/// under the cache hub's system-flagged identity, then re-stamps each subscriber's own
/// <c>AccessService.Context</c> per emission (<c>CarryAccessContext</c>) so RLS is applied
/// for the <i>subscribing</i> user — exactly what a per-user picker wants. The former IoPool
/// load carried NO AccessContext (the pool re-runs the leaf on a ThreadPool worker with no
/// baton), so any node read it did would have run identity-less. Reading through GetQuery
/// fixes that for free.</para>
/// </summary>
public sealed class CopilotModelCatalog
{
    private readonly IMessageHub hub;

    /// <summary>
    /// Creates the catalog, resolving the mesh hub (which owns the shared workspace and synced-query cache).
    /// </summary>
    /// <param name="services">Service provider used to resolve the <see cref="IMessageHub"/>.</param>
    public CopilotModelCatalog(IServiceProvider services)
    {
        // The mesh hub owns the shared workspace + synced-query cache.
        hub = services.GetRequiredService<IMessageHub>();
    }

    /// <summary>
    /// Live Copilot model ids. Emits the current set immediately (warm cache) or on first
    /// cold load, then re-emits whenever the underlying <c>LanguageModel</c> nodes change.
    /// Subscribe / data-bind — never <c>.Take(1)</c> on a display binding (it would freeze).
    /// </summary>
    public IObservable<IReadOnlyList<string>> Models =>
        hub.GetWorkspace()
            // One shared synced query per id, keyed so every consumer reuses the same upstream.
            // Copilot's models live as standard LanguageModel nodes under the "Model" partition,
            // provided by the Copilot provider — filter by nodeType so sibling satellite types
            // under the namespace don't leak into the picker.
            .GetQuery(
                $"{LanguageModelNodeType.NodeType}|Copilot",
                $"namespace:{LanguageModelNodeType.RootNamespace} nodeType:{LanguageModelNodeType.NodeType} provider:Copilot")
            .Select(nodes => (IReadOnlyList<string>)nodes
                .Select(n => n.Id)
                .Where(id => !string.IsNullOrEmpty(id))
                .ToArray())
            // Seed empty so a binding renders immediately (no spinner-forever) and a cold first
            // load doesn't leave CombineLatest-style consumers waiting on a first emission.
            .StartWith(Array.Empty<string>())
            .DistinctUntilChanged();
}
