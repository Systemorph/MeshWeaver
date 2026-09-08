using System.Reactive.Linq;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.PluginCatalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MeshWeaver.Hosting.SelfUpdate;

namespace Memex.Portal.Shared.SelfUpdate;

/// <summary>
/// Wires the module lane's unattended-landing gate (<see cref="IModuleUpdatePolicy"/>, #1664) to
/// the platform's EXISTING update-policy surface — the admin-editable <c>Admin/UpdatePolicy</c>
/// node that already governs the image roll. There is deliberately NO module-specific knob:
///
/// <list type="bullet">
///   <item><b>Continuous</b> — unattended module landing is allowed: store-installed modules track
///     their registry the same way the platform tracks its image. The record's version PATTERN
///     governs the platform IMAGE only (<see cref="UpdateChannelPattern"/>): modules carry their
///     own versions, so a <c>Continuous</c> record without a pattern still lands modules
///     unattended while its platform stays on clean releases.</item>
///   <item><b>Stable</b> (the platform default since 2026-09-08) / <b>None</b> (what an ABSENT
///     node reads as, #3542) — declined: a deployment that pins its image takes updates
///     deliberately, and its modules must not run ahead of that choice. The catalog card's manual
///     Update (and any explicit install) still lands the module — the gate covers only the
///     background reconcile.</item>
/// </list>
///
/// <para>Read through the storage adapter (authoritative point read — the same primitive the
/// package reconcile uses for install records); an unreadable policy DECLINES with the error as the
/// reason, because "could not check the operator's choice" must fail toward not acting.</para>
/// </summary>
public sealed class PlatformModuleUpdatePolicy(IMessageHub hub, ILogger<PlatformModuleUpdatePolicy> logger)
    : IModuleUpdatePolicy
{
    /// <inheritdoc />
    public IObservable<string?> DeclineUnattendedLanding()
    {
        var storage = hub.ServiceProvider.GetService<IStorageAdapter>();
        if (storage is null)
            // No storage = no persisted policy = nothing an operator could have pinned: allowed
            // (a host without storage has no image roll to run ahead of either).
            return Observable.Return<string?>(null);

        return storage.Read(UpdatePolicyNodeType.NodePath, hub.JsonSerializerOptions)
            .Take(1)
            .Select(node =>
            {
                var policy = UpdatePolicyNodeType.Parse(node, hub.JsonSerializerOptions).Policy;
                return policy == UpdatePolicyKind.Continuous
                    ? null
                    : $"the deployment's update policy is {policy} "
                      + $"({UpdatePolicyNodeType.NodePath}) — unattended module updates ride the "
                      + "Continuous channel only; use the catalog's manual Update instead";
            })
            .Catch((Exception ex) =>
            {
                logger.LogWarning(ex,
                    "[SelfUpdate] could not read {Path} — declining unattended module landing "
                    + "this pass.", UpdatePolicyNodeType.NodePath);
                return Observable.Return<string?>(
                    $"the update policy at {UpdatePolicyNodeType.NodePath} could not be read "
                    + $"({ex.GetType().Name})");
            });
    }
}
