using System.Reactive.Linq;
using MeshWeaver.ActionLinks;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Each installed package's own partition counts for action-handler rules — read from the install
/// records the installer already writes (<c>Plugins/{id}</c>, node type <c>Package</c>), the
/// partition being <see cref="PackageInstaller.TargetPartitionOf"/>, the ONE definition of "which
/// partition is this install about". No second list: installing a package IS its declaration, and
/// uninstalling it (the record goes) withdraws it.
///
/// <para>LIVE: one synced query over the records, read as System, so a package installed while the
/// portal runs has its rules counted in the chain's next emission with no recycle.
/// Doc/Architecture/ActionLinkContracts → "Only declared namespaces count".</para>
/// </summary>
internal sealed class InstalledPackageActionHandlerRuleNamespaces : IActionHandlerRuleNamespaceSource
{
    private const string QueryId = "action-handler-rule-namespaces:installed-packages";

    public IObservable<IEnumerable<string>> Namespaces(IMessageHub hub)
    {
        var access = hub.ServiceProvider.GetService<AccessService>();
        var logger = hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger<InstalledPackageActionHandlerRuleNamespaces>();
        return access.RunAsSystem(() => hub.GetQuery(QueryId, CatalogLayoutAreas.AllInstalledQuery))
            .Select(records => (IEnumerable<string>)records
                .Where(record => !string.IsNullOrEmpty(record.Id))
                .Select(record => record.ContentAs<PackageManifest>(hub.JsonSerializerOptions, logger) is { } manifest
                    ? PackageInstaller.TargetPartitionOf(record.Id, manifest)
                    : null)
                .OfType<string>()
                .ToArray());
    }
}
