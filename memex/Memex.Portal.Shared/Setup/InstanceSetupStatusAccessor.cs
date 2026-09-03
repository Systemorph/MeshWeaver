using MeshWeaver.Mesh;

namespace Memex.Portal.Shared.Setup;

/// <summary>
/// Whether THIS instance is still awaiting first-run setup, resolvable from anywhere in the request
/// pipeline.
///
/// <para><b>Plumbed, not re-derived.</b> The decision is made once, in
/// <c>MemexConfiguration.ConfigureMemexMesh</c>: no <c>Graph:Storage</c> configuration and no
/// COMPLETE <c>instance.json</c> ⇒ <c>MarkAwaitingSetup()</c> and an early return that configures no
/// storage, no data sources and no hubs. Re-deriving that here from the same inputs would be a
/// second implementation of one rule, free to disagree with the first — and the disagreement would
/// be a host that either serves the wizard over a live database or refuses to serve the wizard to an
/// instance that has none.</para>
///
/// <para>🚨 <b>The value is read LAZILY, and that is load-bearing.</b> The two portal hosts compose
/// in opposite orders — the monolith configures the portal BEFORE the mesh, the distributed host
/// after — so a value captured at registration time would be <c>false</c> on one of them and correct
/// on the other. The registration is a factory, so the flag is read when the service is first
/// resolved, which is after the whole builder has run either way.</para>
/// </summary>
/// <param name="isAwaitingSetup">Reads <c>MeshBuilder.IsAwaitingSetup</c> at resolve time.</param>
public sealed class InstanceSetupStatusAccessor(Func<bool> isAwaitingSetup)
{
    /// <summary>True when this instance has no storage and no completed setup manifest. False for
    /// every deployment configured through appsettings — which is all of them until an operator
    /// installs an empty image on purpose.</summary>
    public bool IsAwaitingSetup { get; } = (isAwaitingSetup ?? throw new ArgumentNullException(nameof(isAwaitingSetup)))();

    /// <summary>The accessor a host that never registered one answers with — a configured instance.
    ///
    /// <para>Defaulting the OTHER way would park an ordinary host in a setup surface it has no
    /// business showing; a wrong "configured" merely omits a banner.</para></summary>
    public static InstanceSetupStatusAccessor Configured { get; } = new(static () => false);

    /// <summary>Reads the builder's verdict at resolve time.</summary>
    /// <param name="builder">The builder whose <see cref="MeshBuilder.IsAwaitingSetup"/> to read.</param>
    public static InstanceSetupStatusAccessor For(MeshBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return new InstanceSetupStatusAccessor(() => builder.IsAwaitingSetup);
    }
}
