using Microsoft.Extensions.Configuration;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Portal.Shared.SelfUpdate;

/// <summary>Wires the platform self-update feature: the <c>Admin/UpdatePolicy</c> node type, the
/// ACR tag lister + Kubernetes deployment updater seams, and the polling hosted service.</summary>
public static class SelfUpdateConfiguration
{
    /// <summary>Registers the self-update poller and its dependencies. Runs in the Distributed portal
    /// and the Monolith; on a non-Kubernetes host it degrades to detect-and-notify (it records the
    /// available version but patches nothing). NOT registered in the MAUI client (no hosted-service
    /// lifecycle there — MAUI detect-and-notify is handled separately).</summary>
    public static TBuilder AddSelfUpdate<TBuilder>(this TBuilder builder, SelfUpdateOptions? options = null)
        where TBuilder : MeshBuilder
    {
        builder.AddUpdatePolicyType();
        builder.ConfigureServices(services =>
        {
            // Bind from configuration when the caller passes nothing. Without this the defaults were
            // baked into the image: PollInterval sat at 6h with NO way for an environment to change
            // it, and a SelfUpdate__PollInterval in the configmap silently did nothing — the failure
            // mode where an operator sets a knob, sees no effect, and concludes self-update is dead.
            services.AddSingleton(sp =>
                options
                ?? sp.GetService<IConfiguration>()?.GetSection(SelfUpdateOptions.SectionName)
                       .Get<SelfUpdateOptions>()
                ?? new SelfUpdateOptions());
            // The poller lists ACR tags (Azure.Identity) and patches k8s deployments (X.509/TLS) via
            // APIs that are [UnsupportedOSPlatform("browser")]. It is a server-side hosted service and
            // is never wanted in a Blazor WASM client, so skip registration on browser. The guard also
            // satisfies CA1416 for the browser-unsupported impl types without cascading the platform
            // attribute up through AddSelfUpdate's callers (this is a browser-supporting assembly).
            if (!OperatingSystem.IsBrowser())
            {
                services.AddSingleton<IAcrTagLister, AcrTagLister>();
                services.AddSingleton<IDeploymentUpdater, KubernetesDeploymentUpdater>();
                services.AddHostedService<SelfUpdateHostedService>();
            }
            return services;
        });
        return builder;
    }
}
