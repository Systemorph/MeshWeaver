using MeshWeaver.Hosting.SelfUpdate;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

[assembly: MeshWeaver.SelfUpdate.Aks.SelfUpdateAksModule]

namespace MeshWeaver.SelfUpdate.Aks;

/// <summary>
/// The AKS/ACR mechanics behind platform self-update and instance provisioning: reading image tags
/// from an Azure Container Registry, patching Kubernetes Deployments, and provisioning per-instance
/// workloads on the cluster.
///
/// <para>🚨 <b>The self-update POLLER is not part of this module and must never become one.</b>
/// Self-update is how a deployment receives new bits — including new modules — so gating it behind
/// a module creates a bootstrap loop: an install that lost the module could no longer update
/// anything, including re-installing the module. What is genuinely optional is the AKS-SPECIFIC
/// mechanics, and that is exactly what ships here. The poller, the <c>Admin/UpdatePolicy</c> node
/// type, the version-selection logic and the status projection all stay in the platform.</para>
///
/// <para>Without this module an install runs <b>detect-and-notify</b> — the state
/// <c>IDeploymentUpdater.CanPatch == false</c> has always described for a non-Kubernetes host. The
/// platform registers that fallback with <c>TryAdd</c> and this module registers the real
/// implementations with a plain <c>AddSingleton</c>, which resolves correctly in either
/// registration order.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class SelfUpdateAksModuleAttribute : MeshNodeProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<MeshNode> Nodes =>
    [
        new MeshNode("MeshWeaver.SelfUpdate.Aks")
        {
            Name = "AKS self-update and instance provisioning",
            NodeType = "ModuleDefinition",
        }
        .WithGlobalServiceRegistry(services => services.AddSelfUpdateAks()),
    ];

    /// <summary>
    /// The instance-provisioning feature's own registration (options binding + the live cluster-query
    /// service), previously a compiled <c>.AddInstancesAdmin()</c> call in the portal's composition.
    /// </summary>
    public override IEnumerable<Func<MeshBuilder, MeshBuilder>> BuilderConfigurations =>
        [builder => builder.AddInstancesAdmin()];

    /// <summary>
    /// The platform-admin Instances overview tab — live cluster query, Grafana log links and the
    /// guided create-instance plan. Registered on every per-node hub, exactly as the portal's
    /// compiled <c>.AddInstancesAdminSettingsTab()</c> did.
    /// </summary>
    public override IEnumerable<Func<MessageHubConfiguration, MessageHubConfiguration>> DefaultNodeHubConfigurations =>
        [config => config.AddInstancesAdminSettingsTab()];
}

/// <summary>
/// The module's registration surface. Production installs it via <c>Modules:Assemblies</c>
/// (<see cref="SelfUpdateAksModuleAttribute"/>); a fixture or bespoke host calls
/// <see cref="AddSelfUpdateAks"/> for the identical registration.
/// </summary>
public static class SelfUpdateAksExtensions
{
    /// <summary>
    /// Registers the ACR tag reader and the Kubernetes deployment patcher, plus the cluster
    /// instance provisioning service.
    ///
    /// <para>Plain <c>AddSingleton</c> on purpose: the platform registers its detect-and-notify
    /// fallbacks with <c>TryAdd</c>, so the last registration wins for <c>GetRequiredService</c>
    /// and <c>TryAdd</c> declines when one already exists — correct in either order.</para>
    ///
    /// <para>Browser-guarded like the platform's own registration: the credential chain and TLS
    /// APIs these implementations use are <c>[UnsupportedOSPlatform("browser")]</c>, and they are
    /// never wanted in a WASM client.</para>
    /// </summary>
    public static IServiceCollection AddSelfUpdateAks(this IServiceCollection services)
    {
        if (OperatingSystem.IsBrowser())
            return services;

        services.AddSingleton<IAcrTagLister, AcrTagLister>();
        services.AddSingleton<IDeploymentUpdater, KubernetesDeploymentUpdater>();
        // The cluster-query service is registered by AddInstancesAdmin (BuilderConfigurations above),
        // which also binds InstancesOptions — one registration path, not two.
        return services;
    }
}
