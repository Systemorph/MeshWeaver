using Microsoft.Extensions.Configuration;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MeshWeaver.Hosting.SelfUpdate;

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
            // The module lane's unattended-landing gate (#1664) rides the SAME Admin/UpdatePolicy
            // node this feature owns — Continuous (default) lands store-installed modules
            // unattended, Stable/None decline. Registered here, beside the node type, so a host
            // without self-update simply has no policy provider and the PluginCatalog default
            // (allowed) applies. Platform-neutral (a storage read), so no browser guard.
            services.AddSingleton<MeshWeaver.PluginCatalog.IModuleUpdatePolicy, PlatformModuleUpdatePolicy>();
            // The deployment gate (#1754): "may this environment be rolled to that release?".
            // Registered unconditionally — the SAME verdict has to be readable by all three paths
            // that roll a version (the poller below, CD's post-promote assertion and a manual
            // kubectl set image via /api/plugins/is-updatable), and a gate wired for only one of
            // them is not a gate. Platform-neutral (a query plus file-system reads).
            services.AddSingleton<ReleaseAvailabilityService>();
            // 🚨 The COMBO gate (#2274): "can that image still serve the modules this instance has
            // landed?" — the question an artifact check cannot answer, and the one that would have
            // caught the memex.systemorph.com trap. Registered unconditionally and for the same
            // reason as the availability gate: every path that rolls a version has to honour the
            // same verdict. Producing one needs docker (see IComboGateRunner) and is therefore
            // off-cluster; with no runner registered this gate CONSULTS the verdict landed on
            // Admin/UpdatePolicy, and a candidate with no verdict clears nothing.
            // Platform-neutral (a policy read plus, where a runner exists, file-system + process IO).
            services.AddSingleton<ComboVerificationGate>();
            // Bind from configuration when the caller passes nothing. Without this the defaults were
            // baked into the image and a SelfUpdate__* value in the configmap silently did nothing —
            // the failure mode where an operator sets a knob, sees no effect, and concludes
            // self-update is dead. (The former PollInterval is now RetryInterval: the check is
            // event-driven, so the value only paces re-establishing a faulted watch.)
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
                // 🚨 TryAdd, and the AKS/ACR module registers the real ones with a plain AddSingleton.
                // The pairing is ORDER-INDEPENDENT: whichever runs first, the LAST registration of a
                // service type is what GetRequiredService returns and TryAdd declines when any
                // registration already exists. Module listed ⇒ real ACR reader + Kubernetes patcher;
                // module absent ⇒ these fallbacks keep the poller's constructor resolvable and put it
                // in the detect-and-notify state IDeploymentUpdater.CanPatch has always described.
                //
                // The POLLER stays here, never in a module: self-update is how a deployment receives
                // new bits — including modules — so gating it behind one would mean an install that
                // lost the module could not update anything, including re-installing the module.
                services.TryAddSingleton<IAcrTagLister, UnavailableUpdateMechanics.NoRegistry>();
                services.TryAddSingleton<IDeploymentUpdater, UnavailableUpdateMechanics.DetectOnly>();
                services.AddHostedService<SelfUpdateHostedService>();
            }
            return services;
        });
        return builder;
    }
}
