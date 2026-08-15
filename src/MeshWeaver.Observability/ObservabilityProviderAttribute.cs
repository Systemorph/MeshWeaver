using MeshWeaver.Mesh;

[assembly: MeshWeaver.Observability.ObservabilityProvider]

namespace MeshWeaver.Observability;

/// <summary>
/// Boot-pack entry point: loading this assembly via <c>Modules:Assemblies</c> installs red-log
/// ticketing through the SAME <see cref="ObservabilityExtensions.AddLogWatch"/> call a compiled-in
/// portal makes — one code path, so the two registration routes cannot drift. The parameterless
/// call binds <see cref="LogWatchOptions"/> from the host's configuration through the options
/// pipeline; the portal's HTTP ingest surface resolves the engine through the
/// <see cref="ILogIncidentIngest"/> contract, so it maps (token-gated) whether the engine arrived
/// compiled-in or boot-loaded.
/// </summary>
public sealed class ObservabilityProviderAttribute : MeshNodeProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<Func<MeshBuilder, MeshBuilder>> BuilderConfigurations =>
        [builder => builder.AddLogWatch()];
}
