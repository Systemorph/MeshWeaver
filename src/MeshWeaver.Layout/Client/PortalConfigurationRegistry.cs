using System.Collections.Immutable;
using MeshWeaver.Messaging;

namespace MeshWeaver.Layout.Client;

/// <summary>
/// Portal-hub configuration contributed at RUNTIME, by code the image was not built with.
///
/// <para><see cref="LayoutClientConfiguration.PortalConfiguration"/> already carries the same shape
/// of delegate, and <c>PortalApplication</c> folds it into every portal hub. But that list is fixed
/// when the layout client is configured, so only statically-referenced code — the view packs
/// compiled into the image — can add to it. A plugin's assembly is compiled and loaded much later,
/// at NodeType activation, and has no way to participate. This registry is that way.</para>
///
/// <para>Together they let a plugin ship Blazor views: its <c>ConfigureHub</c> calls
/// <see cref="PortalConfigurationExtensions.WithPortalConfiguration"/>, the delegate lands here, and
/// the next portal hub applies it — the same seam
/// <c>layout.WithView&lt;ChartControl, RadzenChartView&gt;()</c> uses.</para>
///
/// <para>Mesh-scoped singleton: registered in the mesh's container so its lifetime IS the mesh's and
/// it dies at teardown. Never static — a process-wide registry would leak view registrations (and
/// the assemblies behind them) across meshes and across tests.</para>
/// </summary>
public sealed class PortalConfigurationRegistry
{
    /// <summary>
    /// 🚨 Keyed by OWNER, and a re-registration REPLACES rather than appends. This is what makes
    /// recompiling a NodeType safe.
    ///
    /// <para>Every recompile mints a new collectible <c>AssemblyLoadContext</c>, so the delegate
    /// registered by build N closes over types from build N's assembly. Appending would leave that
    /// delegate live: every portal hub created afterwards would keep invoking it, which both pins
    /// the old ALC against unload (an ALC leak already caused an OOM on memex-cloud) and mixes two
    /// CLR identities of "the same" view type into one portal. Replacing by owner means the newest
    /// build is the only one that runs, and the previous delegate becomes garbage.</para>
    /// </summary>
    private ImmutableDictionary<string, Func<MessageHubConfiguration, MessageHubConfiguration>> owners
        = ImmutableDictionary<string, Func<MessageHubConfiguration, MessageHubConfiguration>>.Empty;

    /// <summary>
    /// Registers (or replaces) <paramref name="configuration"/> for <paramref name="owner"/>.
    /// </summary>
    /// <param name="owner">Stable identity of the contributor — the node hub's address, so a
    /// recompile of the same NodeType reuses the key and replaces its previous delegate.</param>
    /// <param name="configuration">Applied to every portal hub built after this call.</param>
    public void Set(string owner, Func<MessageHubConfiguration, MessageHubConfiguration> configuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentNullException.ThrowIfNull(configuration);

        ImmutableInterlocked.AddOrUpdate(ref owners, owner, configuration, (_, _) => configuration);
    }

    /// <summary>Drops <paramref name="owner"/>'s contribution, e.g. when its plugin is uninstalled.</summary>
    /// <param name="owner">The key passed to <see cref="Set"/>.</param>
    /// <returns>True when a contribution was present and removed.</returns>
    public bool Remove(string owner) => ImmutableInterlocked.TryRemove(ref owners, owner, out _);

    /// <summary>
    /// The current contributions, ordered by owner.
    ///
    /// <para>Ordered rather than insertion-ordered on purpose: registration order depends on which
    /// NodeType hub happened to activate first, which varies per pod and per boot. Two replicas
    /// applying the same set of contributions in different orders would configure their portals
    /// differently, and the last writer for any one view mapping would differ between them — a
    /// divergence that shows up as "it renders differently on one pod" with nothing to explain
    /// it.</para>
    ///
    /// <para>A snapshot: a portal hub is configured once at creation, so a contribution registered
    /// afterwards applies to the NEXT portal hub, not to live ones. In practice that means a plugin
    /// installed mid-session takes effect on the next page load — reconfiguring a hub that already
    /// has subscribers is a different and much larger problem.</para>
    /// </summary>
    public ImmutableList<PortalContribution> Current =>
        owners.OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new PortalContribution(entry.Key, entry.Value))
            .ToImmutableList();

    /// <summary>The owners currently contributing, ordered — for diagnostics and tests.</summary>
    public ImmutableList<string> Owners =>
        owners.Keys.OrderBy(key => key, StringComparer.Ordinal).ToImmutableList();
}

/// <summary>
/// One contribution and who made it.
///
/// <para>The owner travels WITH the delegate rather than being dropped at the boundary, for two
/// reasons. It is what a log line needs to answer "which plugin configured this portal" — otherwise
/// a misbehaving contribution is an anonymous lambda. And it is the key any future per-user
/// filtering has to select on: which plugins a given viewer may see depends on what is installed and
/// readable for them, so the consumer must be able to drop contributions by owner without the
/// registry knowing anything about access.</para>
/// </summary>
/// <param name="Owner">The contributor's hub address.</param>
/// <param name="Configure">The transform applied to a portal hub.</param>
public sealed record PortalContribution(
    string Owner,
    Func<MessageHubConfiguration, MessageHubConfiguration> Configure);
