using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Storage;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Storage;

/// <summary>
/// The watcher on every <c>StorageBinding</c> node's OWN hub: it validates the binding whenever its
/// target changes (an edit of store, container, prefix or separate account — "validation on save")
/// and answers <see cref="StorageBinding.RequestedAction"/> (Validate / Create), then writes the
/// verdict ON the node: <see cref="StorageBinding.ValidationStatus"/>, its message, the time and the
/// target it is about.
///
/// <para><b>It cannot feed itself.</b> The trigger is <see cref="Trigger"/>, a pure function of the
/// content that is <c>null</c> once the verdict for the current target is recorded and no action is
/// pending — and the verdict write records exactly that, clearing the action. So the hub's own write
/// re-emits a node whose trigger is null and nothing runs again.</para>
///
/// <para><b>Who may ask.</b> Writing the binding at all needs Update on the partition root
/// (<see cref="StorageBindingAccessRule"/>); the hub then acts with the instance's identity — the
/// store's own connection — and records the result as System. A container the partition may not
/// use (<see cref="StorageContainerOwnership"/>), or that is ANOTHER partition's default container,
/// is refused before the store is contacted.</para>
/// </summary>
public static class StorageBindingValidation
{
    /// <summary>
    /// What the watcher should do for <paramref name="binding"/>, or <c>null</c> when nothing is
    /// pending: a requested action (keyed by when it was asked, so a second ask is a second run), or
    /// a re-validation because the target moved since the last verdict. Pure.
    /// </summary>
    /// <param name="binding">The binding's content.</param>
    public static string? Trigger(StorageBinding? binding)
    {
        if (binding is null)
            return null;
        if (!string.IsNullOrEmpty(binding.RequestedAction))
            return $"{binding.RequestedAction}@{binding.RequestedAt:O}|{binding.TargetKey()}|{binding.NewContainerName}";
        return string.Equals(binding.ValidatedTarget, binding.TargetKey(), StringComparison.Ordinal)
            ? null
            : $"{StorageBindingAction.Validate}|{binding.TargetKey()}";
    }

    /// <summary>Installs the watcher on the binding node's own hub (its init turn).</summary>
    /// <param name="hub">The binding node's hub.</param>
    internal static IObservable<Unit> Install(IMessageHub hub)
        => Observable.Defer(() =>
        {
            var logger = hub.ServiceProvider.GetService<ILogger<StorageBinding>>();
            var workspace = hub.GetWorkspace();
            hub.RegisterForDisposal(ActivityControlPlaneExtensions.SubscribeHubWatcher(
                hub,
                () => workspace.GetMeshNodeStream()
                    .Where(node => node is not null)
                    .Select(node => (Node: node!, Key: Trigger(node!.ContentAs<StorageBinding>(hub.JsonSerializerOptions))))
                    .Where(x => x.Key is not null)
                    .DistinctUntilChanged(x => x.Key),
                x => Run(hub, x.Node).Subscribe(
                    _ => { },
                    ex => logger?.LogWarning(ex, "Storage binding {Path}: validation failed to record", x.Node.Path)),
                logger,
                "storage binding validation"));
            return Observable.Return(Unit.Default);
        });

    /// <summary>
    /// Runs the pending action for <paramref name="node"/> against its store and records the verdict
    /// on the node. Emits the recorded binding.
    /// </summary>
    /// <param name="hub">The binding node's hub.</param>
    /// <param name="node">The binding node as read.</param>
    internal static IObservable<StorageBinding> Run(IMessageHub hub, MeshNode node)
    {
        var binding = node.ContentAs<StorageBinding>(hub.JsonSerializerOptions);
        if (binding is null)
            return Observable.Empty<StorageBinding>();
        var partition = StorageBindingPaths.PartitionOf(node.Path);
        var stores = hub.ServiceProvider.GetServices<IInstanceStore>().ToList();
        var create = string.Equals(binding.RequestedAction, StorageBindingAction.Create, StringComparison.Ordinal);
        var asked = binding.RequestedAt;

        return Decide(hub, stores, partition, binding, create)
            .Catch((Exception ex) => Observable.Return(Verdict.Of(
                StorageValidationStatus.Unreachable, $"{ex.GetType().Name}: {ex.Message}", binding.Container)))
            // The verdict is about the target DECIDED ON — recorded as such, so an edit that landed
            // meanwhile leaves ValidatedTarget behind TargetKey and is validated in its own turn.
            .SelectMany(verdict => Record(hub, node.Path, asked, verdict,
                (binding with { Container = verdict.Container ?? binding.Container }).TargetKey()));
    }

    /// <summary>The verdict for one binding (cold). The store is contacted only when the binding
    /// passed every check that needs no store.</summary>
    internal static IObservable<Verdict> Decide(
        IMessageHub hub, IReadOnlyList<IInstanceStore> stores, string partition, StorageBinding binding, bool create)
    {
        if (binding.IsSeparateAccount())
            return Observable.Return(Verdict.Of(StorageValidationStatus.Unverified,
                "A separate account is recorded as a vault reference only. This instance has no connector "
                + "for separate accounts, so it can neither verify nor use it; the binding does not override.",
                binding.Container));

        var store = StorageBindingResolver.StoreFor(stores, binding);
        if (store is null)
            return Observable.Return(Verdict.Of(StorageValidationStatus.Invalid,
                string.IsNullOrWhiteSpace(binding.StoreId)
                    ? $"No pre-configured store serves '{binding.Purpose}' on this instance; pick a store."
                    : $"This instance has no store '{binding.StoreId}'. Available: "
                      + (stores.Count == 0 ? "none" : string.Join(", ", stores.Select(s => s.Id))) + ".",
                binding.Container));

        var name = create && !string.IsNullOrWhiteSpace(binding.NewContainerName)
            ? binding.NewContainerName!.Trim()
            : binding.Container?.Trim();
        if (string.IsNullOrEmpty(name))
            return Observable.Return(Verdict.Of(StorageValidationStatus.Invalid,
                create ? "Name the new container to create." : "Pick a container, or create a new one.", null));

        if (store.ValidateName(name) is { } malformed)
            return Observable.Return(Verdict.Of(StorageValidationStatus.Invalid, malformed, binding.Container));

        if (!StorageContainerOwnership.MayUse(store, partition, name))
            return Observable.Return(Verdict.Of(StorageValidationStatus.Invalid,
                $"'{name}' is not this partition's: in '{store.Id}' the partition '{partition}' may use "
                + $"'{store.DefaultContainerFor(partition)}' or a name starting '{store.ContainerPrefixFor(partition)}_' "
                + $"or '{store.ContainerPrefixFor(partition)}-'.",
                binding.Container));

        return OtherPartitionsDefault(hub, store, partition, name)
            .SelectMany(owner => owner is not null
                ? Observable.Return(Verdict.Of(StorageValidationStatus.Invalid,
                    $"'{name}' is the default container of the partition '{owner}' and cannot be bound here.",
                    binding.Container))
                : (create ? store.EnsureContainer(name) : store.Probe(name)).Select(probe => Verdict.From(probe, store)));
    }

    /// <summary>
    /// The partition (other than <paramref name="partition"/>) whose DEFAULT container in
    /// <paramref name="store"/> is <paramref name="name"/>, or <c>null</c>. Closes the one hole the
    /// pure prefix rule leaves: a partition named <c>acme_x</c> next to a partition <c>acme</c>.
    /// Read as System over the partition catalog — a listing, which is a valid query use.
    /// </summary>
    private static IObservable<string?> OtherPartitionsDefault(
        IMessageHub hub, IInstanceStore store, string partition, string name)
    {
        if (StorageBindingPaths.IsAdmin(partition)
            || string.Equals(name, store.DefaultContainerFor(partition), StringComparison.OrdinalIgnoreCase))
            return Observable.Return<string?>(null);
        var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
        return mesh.Query<MeshNode>(MeshQueryRequest
                .FromQuery($"namespace:{PartitionNodeType.Namespace} nodeType:{PartitionNodeType.NodeType}")
                .AsSystem()
                .Complete())
            .Where(c => c.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
            .Take(1)
            .Select(c => StorageContainerOwnership.OwnerOfDefault(store, partition, name, c.Items.Select(n => n.Id)))
            .DefaultIfEmpty(null);
    }

    /// <summary>Writes the verdict onto the binding node, as System (the owning hub recording its own
    /// state), clearing the action it answered. A second ask that arrived meanwhile is kept.</summary>
    private static IObservable<StorageBinding> Record(
        IMessageHub hub, string path, DateTimeOffset? asked, Verdict verdict, string decidedTarget)
    {
        var access = hub.ServiceProvider.GetService<AccessService>();
        var now = DateTimeOffset.UtcNow;
        return access.RunAsSystem(() => hub.GetWorkspace().GetMeshNodeStream(path).Update<StorageBinding>(b =>
            {
                var answered = b.RequestedAt == asked;
                var next = b with
                {
                    // A created container becomes the binding's — only for the action answered
                    // (an edit meanwhile wins and is validated next).
                    Container = answered && verdict.Status == StorageValidationStatus.Valid && verdict.Container is not null
                        ? verdict.Container
                        : b.Container,
                    NewContainerName = answered && verdict.Created is not null ? null : b.NewContainerName,
                    RequestedAction = answered ? null : b.RequestedAction,
                    RequestedAt = answered ? null : b.RequestedAt,
                    ValidationStatus = verdict.Status,
                    ValidationMessage = verdict.Message,
                    ValidatedAt = now,
                    ValidatedTarget = decidedTarget,
                };
                return next;
            })
            // The recorded binding, read back from what the write produced — never a captured local.
            .Select(node => node.ContentAs<StorageBinding>(hub.JsonSerializerOptions))
            .OfType<StorageBinding>());
    }

    /// <summary>
    /// Asks the binding's own hub to act: writes <paramref name="action"/> as
    /// <see cref="StorageBinding.RequestedAction"/>, through the node stream, as the caller (cold;
    /// subscribe to send). The write needs Update on the partition root like any other edit of the
    /// binding; the hub then acts with the instance's identity.
    /// </summary>
    /// <param name="hub">The hub to write through.</param>
    /// <param name="path">The binding node.</param>
    /// <param name="action">The action (<see cref="StorageBindingAction"/>).</param>
    public static IObservable<MeshNode> RequestAction(IMessageHub hub, string path, string action)
        => hub.GetWorkspace().GetMeshNodeStream(path).Update<StorageBinding>(b =>
            b with { RequestedAction = action, RequestedAt = DateTimeOffset.UtcNow });

    /// <summary>One verdict: the status, why, and the container to record (the created one on a
    /// successful create).</summary>
    internal sealed record Verdict(string Status, string? Message, string? Container, bool? Created)
    {
        /// <summary>A verdict reached without the store.</summary>
        public static Verdict Of(string status, string message, string? container) => new(status, message, container, null);

        /// <summary>The verdict a store's probe implies.</summary>
        public static Verdict From(StorageProbe probe, IInstanceStore store)
        {
            if (probe.NameRefused)
                return new(StorageValidationStatus.Invalid, probe.Message, null, null);
            if (!probe.Reachable)
                return new(StorageValidationStatus.Unreachable,
                    $"'{store.DisplayName}' did not answer: {probe.Message}", null, null);
            if (!probe.Exists)
                return new(StorageValidationStatus.Invalid,
                    $"'{probe.Container}' does not exist in '{store.DisplayName}'. Create it, or pick an existing one."
                    + (string.IsNullOrEmpty(probe.Message) ? "" : $" ({probe.Message})"),
                    null, null);
            if (!probe.Writable)
                return new(StorageValidationStatus.Unreachable,
                    $"'{probe.Container}' exists in '{store.DisplayName}', but the instance's identity may not write to it"
                    + (string.IsNullOrEmpty(probe.Message) ? "." : $": {probe.Message}."),
                    null, null);
            return new(StorageValidationStatus.Valid,
                probe.Created
                    ? $"Created '{probe.Container}' in '{store.DisplayName}' with the instance's identity."
                    : $"'{probe.Container}' exists in '{store.DisplayName}' and is reachable.",
                probe.Container, probe.Created);
        }
    }
}
