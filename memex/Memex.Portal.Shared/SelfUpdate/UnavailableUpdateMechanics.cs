using Microsoft.Extensions.Logging;
using MeshWeaver.Hosting.SelfUpdate;

namespace Memex.Portal.Shared.SelfUpdate;

/// <summary>
/// The fallbacks used when the <c>MeshWeaver.SelfUpdate.Aks</c> module is not listed — i.e. this
/// install is not an AKS/ACR deployment (or deliberately does not roll itself).
///
/// <para>🚨 <b>The poller itself is NOT a module and never becomes one.</b> Self-update is how a
/// deployment receives new bits — including new modules — so putting it behind a module creates a
/// bootstrap loop: an install that lost the module could no longer update anything, including
/// re-installing the module. What IS optional is the AKS-specific MECHANICS (list tags on a
/// container registry, patch a Kubernetes Deployment), and those are what moved.</para>
///
/// <para>The degraded state is not new and not an error path: <see cref="IDeploymentUpdater"/> has
/// always carried <see cref="IDeploymentUpdater.CanPatch"/> precisely so a non-Kubernetes install
/// runs <b>detect-and-notify</b> — it records the available version and patches nothing. These
/// fallbacks are that state, made explicit rather than implied by a missing registration.</para>
/// </summary>
internal static class UnavailableUpdateMechanics
{
    /// <summary>
    /// Reports no tags, so the poller finds no candidate version. Correct for an install with no
    /// container registry to read: the alternative — leaving <see cref="IAcrTagLister"/>
    /// unregistered — makes the poller's constructor unresolvable and takes the whole host down.
    /// </summary>
    internal sealed class NoRegistry(ILogger<SelfUpdateHostedService>? logger = null) : IAcrTagLister
    {
        private bool warned;

        public Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken ct)
        {
            if (!warned)
            {
                warned = true;
                logger?.LogInformation(
                    "Self-update: no container-registry reader is registered, so no candidate version can be "
                    + "found. This is expected unless the deployment lists MeshWeaver.SelfUpdate.Aks under "
                    + "Modules:Assemblies (the module supplies the ACR reader and the Kubernetes patcher).");
            }
            return Task.FromResult<IReadOnlyList<string>>([]);
        }
    }

    /// <summary>
    /// Detect-and-notify: reports <see cref="IDeploymentUpdater.CanPatch"/> false and refuses to
    /// patch. Identical to what a non-Kubernetes install has always done.
    /// </summary>
    internal sealed class DetectOnly : IDeploymentUpdater
    {
        public bool CanPatch => false;

        public Task PatchToVersionAsync(string versionTag, CancellationToken ct) =>
            throw new InvalidOperationException(
                $"Cannot roll this install to '{versionTag}': no deployment patcher is registered. "
                + "CanPatch is false, so the poller must not have called this — list "
                + "MeshWeaver.SelfUpdate.Aks under Modules:Assemblies for a Kubernetes install.");
    }
}
