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
/// <para>A template of framework controls: the stores as a <c>DataGrid</c> (their containers read
/// live), the partition's bindings as a <c>MeshSearch</c> list the client resolves, and an Add button
/// that creates a binding node. Each binding is edited on its OWN page
/// (<see cref="StorageBindingLayoutArea"/>), bound directly to the node.</para>
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
            .WithView(BindingsList(partition));
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

    /// <summary>
    /// The partition's bindings as a query control: the viewer's client runs the query (under the
    /// viewer's own access — a binding is readable only to whoever administers its partition) and
    /// keeps the list live; each entry opens the binding's own page, where it is edited
    /// (<see cref="StorageBindingLayoutArea"/>). A template: nothing is loaded here.
    /// </summary>
    /// <param name="partition">The partition.</param>
    internal static MeshSearchControl BindingsList(string partition)
        => Controls.MeshSearch
            .WithHiddenQuery(StorageBindingPaths.QueryFor(partition) + " sort:name")
            .WithShowSearchBox(false)
            .WithShowEmptyMessage(true)
            .WithRenderMode(MeshSearchRenderMode.List)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithReactiveMode(true);

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
}
