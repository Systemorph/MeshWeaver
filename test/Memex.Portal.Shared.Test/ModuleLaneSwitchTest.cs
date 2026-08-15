using System;
using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Markdown.Export;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Observability;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins the completed lane switch for Markdown.Export + Observability: the portal no longer makes
/// the compiled <c>AddMarkdownExport()</c>/<c>AddLogWatch()</c> calls — listing the DLLs under
/// <c>Modules:Assemblies</c> is the complete activation, folding the SAME registrations through
/// each assembly's <see cref="MeshNodeProviderAttribute"/>.
/// </summary>
public class ModuleLaneSwitchTest
{
    private static IServiceCollection Install(params Type[] anchors)
    {
        var serviceConfigs = new List<Func<IServiceCollection, IServiceCollection>>();
        var builder = new MeshBuilder(configure => serviceConfigs.Add(configure), AddressExtensions.CreateMeshAddress());
        builder.InstallAssemblies(anchors.Select(t => t.Assembly.Location).Distinct().ToArray());
        return serviceConfigs.Aggregate(
            (IServiceCollection)new ServiceCollection(), (collection, configure) => configure(collection));
    }

    [Fact]
    public void MarkdownExportModule_FoldsTheEngineRegistrations()
    {
        var services = Install(typeof(MarkdownExportTemplates));
        Assert.Contains(services, d => d.ServiceType == typeof(MarkdownExportConfig));
        // Both kernel-script assembly registrations (engine + contract) ride the fold.
        var scriptAssemblies = services
            .Where(d => d.ServiceType == typeof(MeshWeaver.Kernel.Hub.KernelScriptAssembly))
            .Select(d => ((MeshWeaver.Kernel.Hub.KernelScriptAssembly)d.ImplementationInstance!).Assembly)
            .ToList();
        Assert.Contains(typeof(MarkdownExportTemplates).Assembly, scriptAssemblies);
        Assert.Contains(typeof(MeshWeaver.Markdown.Export.Messaging.RenderedDocument).Assembly, scriptAssemblies);
    }

    [Fact]
    public void ObservabilityModule_FoldsTheIngestSeam()
    {
        var services = Install(typeof(LogIncidentIngestService));
        Assert.Contains(services, d => d.ServiceType == typeof(LogIncidentIngestService));
        Assert.Contains(services, d => d.ServiceType == typeof(ILogIncidentIngest));
        Assert.Contains(services, d => d.ServiceType == typeof(LogIncidentControlPlane));
    }
}
