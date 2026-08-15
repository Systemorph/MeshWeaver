using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MeshWeaver.Markdown.Export.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Markdown.Export.Test;

/// <summary>
/// Pins the boot-pack contract: installing the export engine assembly via
/// <see cref="MeshBuilder.InstallAssemblies"/> (the <c>Modules:Assemblies</c> path) applies the
/// SAME <c>AddMarkdownExport()</c> a compiled-in portal calls — carried by the attribute's
/// <see cref="MeshNodeProviderAttribute.BuilderConfigurations"/> hook, so the two registration
/// paths are one code path and cannot drift.
/// </summary>
public class ExportBootPackTest
{
    [Fact]
    public void InstallingTheAssembly_AppliesAddMarkdownExport()
    {
        var serviceConfigs = new List<Func<IServiceCollection, IServiceCollection>>();
        var builder = new MeshBuilder(configure => serviceConfigs.Add(configure), AddressExtensions.CreateMeshAddress());

        builder.InstallAssemblies(typeof(MarkdownExportProviderAttribute).Assembly.Location);

        var services = serviceConfigs.Aggregate(
            (IServiceCollection)new ServiceCollection(), (collection, configure) => configure(collection));

        // The engine's DI half landed: the config singleton and BOTH kernel-script assembly
        // registrations (engine + contract — the pair ExportTemplateCompilationTest guards).
        Assert.Contains(services, d => d.ServiceType == typeof(MarkdownExportConfig));
        var scriptAssemblies = services
            .Where(d => d.ServiceType == typeof(MeshWeaver.Kernel.Hub.KernelScriptAssembly))
            .Select(d => ((MeshWeaver.Kernel.Hub.KernelScriptAssembly)d.ImplementationInstance!).Assembly)
            .ToList();
        Assert.Contains(typeof(MarkdownExportTemplates).Assembly, scriptAssemblies);
        Assert.Contains(typeof(Messaging.RenderedDocument).Assembly, scriptAssemblies);
    }

    [Fact]
    public void TheAttribute_CarriesTheBuilderHook()
    {
        var attributes = typeof(MarkdownExportTemplates).Assembly
            .GetCustomAttributes<MeshNodeProviderAttribute>()
            .ToList();
        Assert.Contains(attributes, a => a.BuilderConfigurations.Any());
    }
}
