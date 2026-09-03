using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.PluginCatalog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Memex.Portal.Shared.Setup;

/// <summary>
/// The whole host an instance runs when it has no database — a web application that serves the
/// first-run wizard and nothing else.
///
/// <para>🚨 <b>A SEPARATE host, not a branch inside the portal's pipeline, and the reason is
/// measured rather than aesthetic.</b> Two attempts to serve the wizard from inside the ordinary
/// startup both died before reaching it:</para>
/// <list type="number">
/// <item><c>MapMeshWeaver</c> asserts that a permission evaluator is registered — and on a
/// setup-mode host <c>ConfigureMemexMesh</c> returned before <c>AddRowLevelSecurity</c> ever ran, so
/// the process exited with a security assertion.</item>
/// <item>Past that, <c>EventSubscriptionRunner</c> — an <c>IHostedService</c> a module registers
/// during <c>InstallAssemblies</c>, which happens BEFORE the storage decision — could not resolve
/// <c>IMeshService</c>, and the host failed to start.</item>
/// </list>
/// <para>Both are the same fact seen twice: a portal's startup assumes a configured mesh from very
/// early on, and an instance awaiting setup does not have one. Every fix that keeps the ordinary
/// pipeline is a game of whack-a-mole against every service any module might register. Not building
/// the mesh host at all has no such tail.</para>
///
/// <para><b>The decision is made from configuration alone</b>, before any mesh is composed — which
/// it can be, because the manifest has already been layered into configuration by then. That is the
/// same input <c>MemexConfiguration</c> uses for <c>MarkAwaitingSetup</c>, so the two cannot
/// disagree about whether this instance is configured.</para>
/// </summary>
public static class SetupOnlyHost
{
    /// <summary>
    /// Whether this instance has no storage and must therefore be set up.
    ///
    /// <para>🚨 <b>An ABSENT <c>Graph:Storage</c> section, not an empty one</b> —
    /// <c>GraphStorageConfig.Type</c> defaults to <c>FileSystem</c>, so a section that EXISTS but
    /// says nothing binds to a working-looking file-system configuration and would boot straight
    /// past setup onto container-ephemeral disk. A blank <c>Type</c> is treated as absent for the
    /// same reason: an environment variable cannot be null, only empty.</para>
    /// </summary>
    /// <param name="configuration">The host's configuration, with the instance manifest already
    /// layered in (see <see cref="InstanceManifestConfigurationExtensions.AddInstanceManifest"/>).</param>
    public static bool IsAwaitingSetup(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var storage = configuration.GetSection(InstanceManifestProjection.StorageSection)
            .Get<GraphStorageConfig>();
        return storage is null || string.IsNullOrWhiteSpace(storage.Type);
    }

    /// <summary>
    /// Runs the setup-only host when this instance is awaiting setup, and answers whether it did.
    ///
    /// <para>Call this FIRST in a host's <c>Program.cs</c> — before any mesh configuration, before
    /// module installation, before the portal's own service registration — and return immediately
    /// when it answers true. The call BLOCKS for the lifetime of the wizard: the operator submits
    /// the form, the manifest is written, and the process stops so its supervisor restarts it into a
    /// configured mesh.</para>
    /// </summary>
    /// <param name="builder">The application builder, with configuration already loaded.</param>
    /// <param name="catalog">Supplies what this image can offer. Invoked only in setup mode, so a
    /// host pays nothing for it when configured.</param>
    /// <param name="registerBackends">Registers the keyed <c>IStorageAdapterFactory</c> services
    /// this image ships, so the wizard offers exactly the backends it can actually open. Modules are
    /// NOT installed in setup mode, so a module-supplied backend is deliberately not on offer: an
    /// instance cannot be set up onto a backend whose assembly it has not got.</param>
    /// <returns>True when the wizard ran (and has now finished); false when this instance is
    /// configured and the caller should carry on with its ordinary startup.</returns>
    public static bool TryRun(
        WebApplicationBuilder builder,
        Func<IServiceProvider, ISetupCatalogProvider> catalog,
        Action<IServiceCollection> registerBackends)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(registerBackends);

        if (!IsAwaitingSetup(builder.Configuration))
            return false;

        registerBackends(builder.Services);
        builder.Services.TryAddSingleton(sp => StorageBackendCatalog.Discover(builder.Services));
        builder.Services.TryAddSingleton<SetupAccessToken>();
        builder.Services.TryAddSingleton(catalog);
        // The status the surface itself gates on. Constant here — this host exists only in the
        // awaiting-setup state, and re-deriving the answer a second time is how two code paths
        // start disagreeing about whether an instance is configured.
        builder.Services.TryAddSingleton(new InstanceSetupStatusAccessor(static () => true));

        var app = builder.Build();
        // The probe FIRST, so an orchestrator does not kill the very pod being configured. An
        // instance awaiting setup is not failing; it is waiting for a person.
        app.MapGet("/healthz", () => Results.Text("ok"));
        app.MapInstanceSetup();
        app.Run();
        return true;
    }

    /// <summary>
    /// The message a host logs when it hands over to the wizard, so the reason appears in the log
    /// even when the operator only ever looks at the pod's output.
    /// </summary>
    /// <param name="rootDirectory">The writable root the manifest will be written to.</param>
    public static string HandOverBanner(string rootDirectory) =>
        $"This instance has no Graph:Storage configuration and no completed setup manifest at "
        + $"{InstanceManifest.PathFor(rootDirectory)}. Serving the FIRST-RUN SETUP wizard and "
        + "nothing else — the mesh is deliberately not composed, because a portal's startup assumes "
        + "a configured store from very early on.";
}
