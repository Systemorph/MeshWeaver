using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Storage;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Storage;

/// <summary>
/// A storage binding's own page — a TEMPLATE bound to the node: the verdict the binding's hub
/// recorded (read-only fields of the node), the editor (a <see cref="MeshNodeContentEditorControl"/>
/// the client binds DIRECTLY to the node and writes back through the node stream — no <c>/data</c>
/// replica, no save loop), and the Create / Validate / delete actions, which write
/// <see cref="StorageBinding.RequestedAction"/> for the node's hub to act on. Deleting a binding is
/// the node's own Delete (its menu).
///
/// <para>Nothing of the node is read here. The dropdowns' option lists are the instance's stores and
/// the containers this partition may bind in them, read LIVE from the stores — so a container created
/// a moment ago is offered at once — and the partition comes from the node's own path.</para>
/// </summary>
public static class StorageBindingLayoutArea
{
    /// <summary>The area name.</summary>
    public const string PageArea = "Binding";

    /// <summary>Registers the page as the binding's default and edit view.</summary>
    /// <param name="config">The binding node's hub configuration.</param>
    public static MessageHubConfiguration AddStorageBindingViews(this MessageHubConfiguration config)
        => config.AddLayout(layout => layout
            .WithNodePage(PageArea, Render)
            .WithView(MeshNodeLayoutAreas.EditArea, Render)
            .WithDefaultArea(PageArea));

    /// <summary>Renders the page.</summary>
    /// <param name="host">The layout host on the binding's own hub.</param>
    /// <param name="_">The rendering context.</param>
    public static IObservable<UiControl?> Render(LayoutAreaHost host, RenderingContext _)
    {
        var path = host.Hub.Address.ToString();
        var partition = StorageBindingPaths.PartitionOf(path);
        var stores = host.Hub.ServiceProvider.GetServices<IInstanceStore>().ToImmutableList();

        var containers = stores.IsEmpty
            ? Observable.Return(ImmutableList<(string Store, string Container)>.Empty)
            : stores.Select(store => store.ListContainers()
                    .Select(all => StorageContainerOwnership.Usable(store, partition, all)
                        .Select(c => (Store: store.Id, Container: c)).ToImmutableList())
                    .Catch((Exception _) => Observable.Return(ImmutableList<(string Store, string Container)>.Empty)))
                .CombineLatest()
                .Select(lists => lists.SelectMany(l => l).ToImmutableList());

        return containers.Select(usable => (UiControl?)Controls.Stack
                .WithVerticalGap(12)
                .WithView(Controls.H2(host.Localize("settings.storageBinding")))
                .WithView(Verdict(host, path))
                .WithView(Editor(host, stores, path, usable))
                .WithView(Controls.Stack.WithOrientation(Orientation.Horizontal).WithHorizontalGap(8)
                    .WithView(ActionButton(host, path, StorageBindingAction.Create, "settings.storageCreate"))
                    .WithView(ActionButton(host, path, StorageBindingAction.Validate, "settings.storageValidate"))))
            .StartWith((UiControl?)Controls.Markdown(host.Localize("ui.mdLoading")));
    }

    private static readonly ImmutableList<string> Statuses =
    [
        StorageValidationStatus.Pending, StorageValidationStatus.Valid, StorageValidationStatus.Invalid,
        StorageValidationStatus.Unreachable, StorageValidationStatus.Unverified,
    ];

    /// <summary>The verdict, read-only, bound to the node.</summary>
    private static UiControl Verdict(LayoutAreaHost host, string path)
        => new MeshNodeContentEditorControl(path)
        {
            CanEdit = false,
            Fields =
            [
                new MeshNodeEditorField("validationStatus", host.Localize("settings.storageFieldStatus"), MeshNodeEditorFieldKind.Enum)
                {
                    Options = Statuses,
                    OptionLabels = Statuses.ToImmutableDictionary(s => s, s => host.Localize("settings.storageStatus." + s)),
                },
                new MeshNodeEditorField("validationMessage", host.Localize("settings.storageFieldMessage"), MeshNodeEditorFieldKind.Text),
            ],
        };

    /// <summary>
    /// The editor, bound to the node. Purpose, store and container are dropdowns: the purposes the
    /// platform offers, the instance's stores (empty = the purpose's default store), and the
    /// containers this partition may bind, each labelled with the stores that hold it.
    /// </summary>
    internal static MeshNodeContentEditorControl Editor(
        LayoutAreaHost host, ImmutableList<IInstanceStore> stores, string path,
        ImmutableList<(string Store, string Container)> usable)
    {
        var containers = usable.Select(u => u.Container).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToImmutableList();
        var containerLabels = containers.ToImmutableDictionary(c => c,
            c => $"{c} — {string.Join(", ", usable.Where(u => u.Container == c).Select(u => u.Store))}");
        var fields = ImmutableList.Create(
            Enum(host, "purpose", "settings.storageFieldPurpose", [.. StoragePurpose.Known]),
            Enum(host, "storeId", "settings.storageFieldStore", ImmutableList.Create("").AddRange(stores.Select(s => s.Id)))
                with
                {
                    OptionLabels = stores.ToImmutableDictionary(s => s.Id, s => $"{s.DisplayName} ({s.Kind})")
                        .Add("", host.Localize("settings.storageDefaultStore")),
                },
            Enum(host, "container", "settings.storageFieldContainer", containers) with { OptionLabels = containerLabels },
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

    /// <summary>A button that asks the binding's own hub to act (<see cref="StorageBindingValidation.RequestAction"/>).</summary>
    private static UiControl ActionButton(LayoutAreaHost host, string path, string action, string labelKey)
        => Controls.Button(host.Localize(labelKey))
            .WithClickAction(ctx =>
            {
                var hub = ctx.Host.Hub;
                var logger = hub.ServiceProvider.GetService<ILogger<StorageBinding>>();
                StorageBindingValidation.RequestAction(hub, path, action)
                    .Subscribe(_ => { }, ex => logger?.LogWarning(ex, "Storage binding {Path}: {Action} could not be requested", path, action));
                return Task.CompletedTask;
            });
}
