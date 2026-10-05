using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The package page's surface for a module reload (<c>Doc/Architecture/ModuleReload</c>): a node-menu
/// entry on an installed package that declares a compiled module, and the area it opens — one
/// sentence saying what will happen and one button that files the request. Framework controls only;
/// every visible string is a catalog key (en + de). Global admins only: the menu entry is not
/// offered to anyone else and the button re-checks before it writes.
/// </summary>
public static class ModuleReloadViews
{
    /// <summary>The area on a package node that offers the reload.</summary>
    public const string ReloadArea = "ReloadModule";

    /// <summary>Registers the area and the menu entry on the package node type's hub.</summary>
    /// <param name="config">The package node type's hub configuration.</param>
    public static MessageHubConfiguration AddModuleReloadViews(this MessageHubConfiguration config)
        => config
            .AddLayout(layout => layout.WithView(ReloadArea, Reload))
            .AddNodeMenuItems(MenuItems);

    /// <summary>The module an install record declares, or null.</summary>
    private static IObservable<string?> DeclaredModule(LayoutAreaHost host) =>
        host.Workspace.GetMeshNodeStream()
            .Select(node => node.ContentAs<PackageManifest>(host.Hub.JsonSerializerOptions)?.Module is { Length: > 0 } module
                ? module.Trim()
                : null)
            .DistinctUntilChanged();

    /// <summary>The menu entry — only on a package that declares a module, and only for a global admin.</summary>
    private static IObservable<IReadOnlyCollection<NodeMenuItemDefinition>> MenuItems(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        return DeclaredModule(host).CombineLatest(host.Hub.IsGlobalAdmin(), (module, admin) =>
            (IReadOnlyCollection<NodeMenuItemDefinition>)(module is null || !admin
                ? []
                :
                [
                    new NodeMenuItemDefinition(
                        "Reload module", ReloadArea,
                        Icon: "🔄", Order: 40,
                        Href: MeshNodeLayoutAreas.BuildUrl(hubPath, ReloadArea),
                        Tooltip: "Fetch the newest compatible published version of this module and activate it on this instance")
                    {
                        LabelKey = "menu.reloadModule",
                        TooltipKey = "menu.reloadModuleTooltip",
                    },
                ]));
    }

    /// <summary>The reload area: what will happen, and the button that files the request.</summary>
    /// <param name="host">The package node's layout host.</param>
    /// <param name="_">The rendering context.</param>
    public static IObservable<UiControl?> Reload(LayoutAreaHost host, RenderingContext _) =>
        DeclaredModule(host).CombineLatest(host.Hub.IsGlobalAdmin(), (module, admin) => (UiControl?)(module is null
            ? Controls.Markdown(host.Localize("moduleReload.noModule"))
            : !admin
                ? Controls.Markdown(host.Localize("moduleReload.adminsOnly"))
                : Controls.Stack
                    .WithView(Controls.Title(host.Localize("moduleReload.title", module), 2))
                    .WithView(Controls.Markdown(host.Localize("moduleReload.explain", module)))
                    .WithView(Controls.Button("🔄 " + host.Localize("moduleReload.button"))
                        .WithAppearance(Appearance.Accent)
                        .WithClickAction(ctx => File(ctx, module)), "Reload")));

    /// <summary>Files the request as the clicking global admin, then opens the request's page.</summary>
    private static Task File(UiActionContext ctx, string module)
    {
        var logger = ctx.Hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(ModuleReloadViews));
        var requestedBy = ctx.Hub.ServiceProvider.GetService<AccessService>()?.Context?.ObjectId is { Length: > 0 } id ? id : null;
        ctx.Hub.IsGlobalAdmin().Take(1)
            .SelectMany(admin => admin
                ? ModuleReload.Request(ctx.Hub, new ModuleReloadRequest
                {
                    Module = module,
                    Reason = $"requested from the package page by {requestedBy ?? "a platform admin"}",
                    RequestedBy = requestedBy,
                })
                : Observable.Return(new ModuleReloadTicket(null, "only a platform admin may reload a module")))
            .Subscribe(
                ticket =>
                {
                    if (ticket.Path is { } path)
                        ctx.NavigateTo($"/{path}");
                    else
                        logger?.LogWarning("[ModuleReload] the reload of {Module} was not filed: {Refusal}", module, ticket.Refusal);
                },
                ex => logger?.LogWarning(ex, "[ModuleReload] the reload of {Module} could not be filed", module));
        return Task.CompletedTask;
    }
}
