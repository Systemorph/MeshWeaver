using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataGrid;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Storage;
using MeshWeaver.Messaging;
using MeshWeaver.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Storage;

/// <summary>
/// The <b>Storage</b> settings section — two surfaces over ONE content builder:
/// <list type="bullet">
///   <item>the instance settings (the Admin app, <c>/Admin/Settings/Storage</c>), where a platform
///     admin binds the GLOBAL storage — the <c>Admin</c> partition's own bindings at
///     <c>Admin/_Storage</c>;</item>
///   <item>a partition's own settings (its root, <c>/{partition}/Settings/Storage</c>), where whoever
///     administers that partition (Update on its root, by ordinary grants) binds its storage at
///     <c>{partition}/_Storage</c>. A platform admin gets nothing here by that role.</item>
/// </list>
///
/// <para>Everything on it is a framework control bound to the nodes: the stores as a
/// <c>DataGrid</c>, each binding as a <see cref="MeshNodeContentEditorControl"/> bound DIRECTLY to its
/// node (the store and container choices are dropdowns over the stores and the containers read LIVE
/// from the chosen store), and the actions as buttons that write the binding's
/// <see cref="StorageBinding.RequestedAction"/> for its own hub to act on. No <c>/data</c> replica,
/// no save loop: the editor writes the node, the node's hub validates the edit and records the
/// verdict on the node, and this section re-renders from the node.</para>
/// </summary>
public static class StorageSettingsTab
{
    /// <summary>The tab id on both surfaces.</summary>
    public const string TabId = "Storage";

    /// <summary>The tab on the Admin app (platform admins only, via <see cref="AdminAppNodeType.AddAdminAppTab"/>).</summary>
    public static SettingsMenuItemDefinition AdminDefinition { get; } = new(
        Id: TabId,
        Label: "Storage",
        ContentBuilder: BuildContent,
        Group: AdminAppNodeType.OperationsGroup,
        Icon: FluentIcons.Database(),
        GroupIcon: FluentIcons.Wrench(),
        Order: AdminAppNodeType.OperationsOrder + 35,
        Keywords: ["storage", "schema", "container", "blob", "postgres", "database", "bind", "account", "vault"])
    { LabelKey = "settings.storage", GroupKey = AdminAppNodeType.OperationsGroupKey };

    /// <summary>The tab on a partition root's settings (Update on that root).</summary>
    public static SettingsMenuItemDefinition PartitionDefinition { get; } = new(
        Id: TabId,
        Label: "Storage",
        ContentBuilder: BuildContent,
        Icon: FluentIcons.Database(),
        Order: 260,
        RequiredPermission: Permission.Update,
        Keywords: ["storage", "schema", "container", "blob", "postgres", "database", "bind"])
    { LabelKey = "settings.storage" };

    /// <summary>
    /// Registers the partition surface on every node hub — offered on a partition ROOT only, and
    /// never on the Admin app, which carries its own (<see cref="AdminDefinition"/>).
    /// </summary>
    /// <param name="config">The default node-hub configuration.</param>
    public static MessageHubConfiguration AddPartitionStorageSettingsTab(this MessageHubConfiguration config)
        => config
            .AddSettingsMenuItems(new SettingsMenuItemProvider((host, _) =>
                Observable.Return<IReadOnlyList<SettingsMenuItemDefinition>>(
                    host.IsAdminAppHub() ? [] : [PartitionDefinition])))
            .RestrictSettingsTabsToPartitionRoot(TabId);

    /// <summary>One row of the stores grid.</summary>
    /// <param name="Store">The store's display name.</param>
    /// <param name="Id">The store id.</param>
    /// <param name="Kind">Its kind.</param>
    /// <param name="DefaultFor">The purposes it serves with no binding.</param>
    /// <param name="DefaultContainer">This partition's default container in it.</param>
    /// <param name="Containers">How many of its containers this partition may bind (read live).</param>
    public sealed record StoreRow(string Store, string Id, string Kind, string DefaultFor, string DefaultContainer, string Containers);

    /// <summary>The content: the stores, then the bindings, of the partition the page is on.</summary>
    internal static UiControl BuildContent(LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        var partition = StorageBindingPaths.PartitionOf(host.Hub.Address.ToString());
        var stores = host.Hub.ServiceProvider.GetServices<IInstanceStore>().ToImmutableList();
        // The viewer's zone, resolved ONCE on the render turn and handed down (the AsyncLocal reads
        // silently-UTC after the query hop below).
        var zoneId = host.Hub.ServiceProvider.GetService<AccessService>().ViewerZoneId();

        stack = stack
            .WithView(Controls.H2(host.Localize("settings.storage")).WithStyle("margin: 0 0 4px 0;"))
            .WithView(Controls.Markdown(host.Localize(StorageBindingPaths.IsAdmin(partition)
                ? "settings.storageIntroAdmin"
                : "settings.storageIntroPartition", partition)));

        if (string.IsNullOrEmpty(partition))
            return stack;

        return stack
            .WithView(Controls.H3(host.Localize("settings.storageStores")))
            .WithView((h, _) => StoresGrid(h, stores, partition))
            .WithView(Controls.H3(host.Localize("settings.storageBindings")))
            .WithView(AddButton(host, partition))
            .WithView((h, _) => BindingSections(h, stores, partition, zoneId));
    }

    // ── Stores ──────────────────────────────────────────────────────────────────────────────────

    private static IObservable<UiControl?> StoresGrid(LayoutAreaHost host, ImmutableList<IInstanceStore> stores, string partition)
    {
        if (stores.IsEmpty)
            return Observable.Return<UiControl?>(Controls.Markdown(host.Localize("settings.storageNoStores")));
        var rows = stores.Select(store => store.ListContainers()
                .Select(all => StorageContainerOwnership.Usable(store, partition, all).Count.ToString())
                .Catch((Exception ex) => Observable.Return(host.Localize("settings.storageUnreachable", ex.Message)))
                .Select(count => new StoreRow(store.DisplayName, store.Id, store.Kind,
                    string.Join(", ", store.DefaultPurposes.Order(StringComparer.Ordinal)),
                    store.DefaultContainerFor(partition), count)))
            .ToList();
        return rows.CombineLatest()
            .Select(list => (UiControl?)Controls.DataGrid(list.ToImmutableList())
                .WithColumn(Column(host, nameof(StoreRow.Store), "settings.storageColStore"))
                .WithColumn(Column(host, nameof(StoreRow.Id), "settings.storageColId"))
                .WithColumn(Column(host, nameof(StoreRow.Kind), "settings.storageColKind"))
                .WithColumn(Column(host, nameof(StoreRow.DefaultFor), "settings.storageColDefaultFor"))
                .WithColumn(Column(host, nameof(StoreRow.DefaultContainer), "settings.storageColDefaultContainer"))
                .WithColumn(Column(host, nameof(StoreRow.Containers), "settings.storageColContainers"))
                .Resizable())
            .StartWith((UiControl?)Controls.Markdown(host.Localize("ui.mdLoading")));
    }

    private static PropertyColumnControl Column(LayoutAreaHost host, string property, string titleKey)
        => new PropertyColumnControl<string> { Property = property.ToCamelCase() }.WithTitle(host.Localize(titleKey));

    // ── Bindings ────────────────────────────────────────────────────────────────────────────────

    private static UiControl AddButton(LayoutAreaHost host, string partition)
        => Controls.Button("➕ " + host.Localize("settings.storageAdd"))
            .WithAppearance(Appearance.Accent)
            .WithStyle("align-self: flex-start;")
            .WithClickAction(ctx =>
            {
                var logger = ctx.Host.Hub.ServiceProvider.GetService<ILogger<StorageBinding>>();
                ctx.Host.Hub.ServiceProvider.GetRequiredService<IMeshService>()
                    .CreateNode(NewBinding(partition))
                    .Subscribe(_ => { }, ex => logger?.LogWarning(ex, "Adding a storage binding to {Partition} failed", partition));
                return Task.CompletedTask;
            });

    /// <summary>A fresh binding node for <paramref name="partition"/> — the partition's default
    /// doc-part location, pending until a container is picked or created.</summary>
    /// <param name="partition">The partition.</param>
    public static MeshNode NewBinding(string partition)
    {
        var id = $"binding-{Guid.NewGuid():N}"[..16];
        return new MeshNode(id, StorageBindingPaths.NamespaceOf(partition))
        {
            NodeType = StorageBindingPaths.NodeType,
            Name = "Storage binding",
            MainNode = partition,
            Content = new StorageBinding(),
        };
    }

    private static IObservable<UiControl?> BindingSections(
        LayoutAreaHost host, ImmutableList<IInstanceStore> stores, string partition, string? zoneId)
    {
        var mesh = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();
        return mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(StorageBindingPaths.QueryFor(partition)).Complete())
            .Scan(ImmutableDictionary<string, MeshNode>.Empty, (rows, change) =>
            {
                if (change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
                    rows = ImmutableDictionary<string, MeshNode>.Empty;
                foreach (var item in change.Items)
                    rows = change.ChangeType == QueryChangeType.Removed ? rows.Remove(item.Path) : rows.SetItem(item.Path, item);
                return rows;
            })
            .Select(rows => rows.Values.OrderBy(n => n.Path, StringComparer.Ordinal).ToImmutableList())
            .Select(nodes => nodes.IsEmpty
                ? Observable.Return<UiControl?>(Controls.Markdown(host.Localize("settings.storageNoBindings")))
                : nodes.Select(n => BindingSection(host, stores, partition, n, zoneId)).CombineLatest()
                    .Select(sections => (UiControl?)sections.Aggregate(
                        Controls.Stack.WithVerticalGap(16), (s, section) => s.WithView(section))))
            .Switch()
            .Catch((Exception ex) => Observable.Return<UiControl?>(
                Controls.Markdown(host.Localize("settings.storageReadFailed", ex.Message))))
            .StartWith((UiControl?)Controls.Markdown(host.Localize("ui.mdLoading")));
    }

    private static IObservable<UiControl> BindingSection(
        LayoutAreaHost host, ImmutableList<IInstanceStore> stores, string partition, MeshNode node, string? zoneId)
    {
        var binding = node.ContentAs<StorageBinding>(host.Hub.JsonSerializerOptions) ?? new StorageBinding();
        var store = StorageBindingResolver.StoreFor(stores, binding);
        var containers = store is null
            ? Observable.Return(ImmutableList<string>.Empty)
            : store.ListContainers()
                .Select(all => StorageContainerOwnership.Usable(store, partition, all))
                .Catch((Exception _) => Observable.Return(ImmutableList<string>.Empty));
        return containers.Select(usable => (UiControl)Controls.Stack
            .WithVerticalGap(6)
            .WithView(Controls.H4($"{binding.Purpose} · {node.Name ?? node.Id}"))
            .WithView(Controls.Markdown(StatusLine(host, binding, zoneId)))
            .WithView(Editor(host, stores, node.Path, binding, usable))
            .WithView(Controls.Stack.WithOrientation(Orientation.Horizontal).WithHorizontalGap(8)
                .WithView(ActionButton(host, node.Path, StorageBindingAction.Create, "settings.storageCreate"))
                .WithView(ActionButton(host, node.Path, StorageBindingAction.Validate, "settings.storageValidate"))
                .WithView(DeleteButton(host, node.Path))));
    }

    private static readonly ImmutableHashSet<string> KnownStatuses =
    [
        StorageValidationStatus.Pending, StorageValidationStatus.Valid, StorageValidationStatus.Invalid,
        StorageValidationStatus.Unreachable, StorageValidationStatus.Unverified,
    ];

    private static string StatusLine(LayoutAreaHost host, StorageBinding binding, string? zoneId)
    {
        // Open vocabulary: a status this build has no label for is shown as written, never as a raw key.
        var status = KnownStatuses.Contains(binding.ValidationStatus)
            ? host.Localize("settings.storageStatus." + binding.ValidationStatus)
            : binding.ValidationStatus;
        var when = binding.ValidatedAt is { } at
            ? " · " + DisplayTimeExtensions.ToDisplayTime(at, zoneId).ToString("yyyy-MM-dd HH:mm zzz")
            : "";
        var stale = binding.IsUsable() || binding.ValidationStatus != StorageValidationStatus.Valid
            ? ""
            : " · " + host.Localize("settings.storageRevalidating");
        return $"**{status}**{when}{stale}" + (string.IsNullOrEmpty(binding.ValidationMessage) ? "" : $"  \n{binding.ValidationMessage}");
    }

    /// <summary>
    /// The binding's editor, bound to the node. Purpose, store and container are dropdowns: the
    /// purposes the platform offers (plus the binding's own, the vocabulary being open), the
    /// instance's stores, and the containers of the chosen store this partition may bind — read
    /// live, so a container created a moment ago is offered at once.
    /// </summary>
    internal static MeshNodeContentEditorControl Editor(
        LayoutAreaHost host, ImmutableList<IInstanceStore> stores, string path, StorageBinding binding, ImmutableList<string> usable)
    {
        var purposes = StoragePurpose.Known.Contains(binding.Purpose)
            ? StoragePurpose.Known.ToImmutableList()
            : StoragePurpose.Known.ToImmutableList().Add(binding.Purpose);
        var containers = string.IsNullOrEmpty(binding.Container) || usable.Contains(binding.Container)
            ? usable
            : usable.Add(binding.Container);
        var defaultStoreLabel = host.Localize("settings.storageDefaultStore");
        var fields = ImmutableList.Create(
            Enum(host, "purpose", "settings.storageFieldPurpose", purposes),
            Enum(host, "storeId", "settings.storageFieldStore", ImmutableList.Create("").AddRange(stores.Select(s => s.Id)))
                with
                {
                    OptionLabels = stores.ToImmutableDictionary(s => s.Id, s => $"{s.DisplayName} ({s.Kind})")
                        .Add("", defaultStoreLabel),
                },
            Enum(host, "container", "settings.storageFieldContainer", containers),
            Text(host, "newContainerName", "settings.storageFieldNewContainer"),
            Text(host, "tablePrefix", "settings.storageFieldTablePrefix"),
            Text(host, "collection", "settings.storageFieldCollection"),
            new MeshNodeEditorField("isDefault", host.Localize("settings.storageFieldIsDefault"), MeshNodeEditorFieldKind.Bool),
            Text(host, "vaultSecretName", "settings.storageFieldVaultSecret"),
            Text(host, "managedIdentityClientId", "settings.storageFieldManagedIdentity"),
            Text(host, "externalEndpoint", "settings.storageFieldEndpoint"));
        return new MeshNodeContentEditorControl(path) { Fields = fields };
    }

    private static MeshNodeEditorField Enum(LayoutAreaHost host, string key, string labelKey, ImmutableList<string> options)
        => new(key, host.Localize(labelKey), MeshNodeEditorFieldKind.Enum) { Options = options };

    private static MeshNodeEditorField Text(LayoutAreaHost host, string key, string labelKey)
        => new(key, host.Localize(labelKey), MeshNodeEditorFieldKind.Text);

    /// <summary>A button that asks the binding's own hub to act: it writes
    /// <see cref="StorageBinding.RequestedAction"/> on the node, through the node stream, as the clicker.</summary>
    private static UiControl ActionButton(LayoutAreaHost host, string path, string action, string labelKey)
        => Controls.Button(host.Localize(labelKey))
            .WithClickAction(ctx =>
            {
                var hub = ctx.Host.Hub;
                var logger = hub.ServiceProvider.GetService<ILogger<StorageBinding>>();
                RequestAction(hub, path, action)
                    .Subscribe(_ => { }, ex => logger?.LogWarning(ex, "Storage binding {Path}: {Action} could not be requested", path, action));
                return Task.CompletedTask;
            });

    /// <summary>Writes <paramref name="action"/> as the binding's request (cold; subscribe to send).</summary>
    /// <param name="hub">The hub to write through.</param>
    /// <param name="path">The binding node.</param>
    /// <param name="action">The action (<see cref="StorageBindingAction"/>).</param>
    public static IObservable<MeshNode> RequestAction(IMessageHub hub, string path, string action)
        => hub.GetWorkspace().GetMeshNodeStream(path).Update(current =>
        {
            var binding = current.ContentAs<StorageBinding>(hub.JsonSerializerOptions) ?? new StorageBinding();
            return current with { Content = binding with { RequestedAction = action, RequestedAt = DateTimeOffset.UtcNow } };
        });

    private static UiControl DeleteButton(LayoutAreaHost host, string path)
        => Controls.Button("🗑️")
            .WithLabel(host.Localize("settings.storageDelete"))
            .WithClickAction(ctx =>
            {
                var hub = ctx.Host.Hub;
                var logger = hub.ServiceProvider.GetService<ILogger<StorageBinding>>();
                hub.ServiceProvider.GetRequiredService<IMeshService>().DeleteNode(path)
                    .Subscribe(_ => { }, ex => logger?.LogWarning(ex, "Deleting storage binding {Path} failed", path));
                return Task.CompletedTask;
            });
}
