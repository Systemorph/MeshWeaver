using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The import stack's deployment-shape gate. <c>MeshWeaver.Import</c> ships as a
/// <c>modules/&lt;Name&gt;/</c> closure with its ENTIRE private reader family inside it — the six
/// <c>MeshWeaver.DataSetReader.*</c> assemblies plus <c>CsvHelper</c>, none of which any host
/// references — so the folder either loads as a unit or the first spreadsheet import faults.
///
/// <para>🚨 It has to be gated HERE, not from a portal boot. <c>Memex.Portal.Monolith</c> bundles
/// the Northwind SAMPLE, whose data sources call <c>AddImport()</c>, so Import rides that host's
/// app closure and <c>ResolveModulePath</c> falls back to the app folder — booting it proves the
/// module list resolves but says NOTHING about the module-only layout every other deployment runs.
/// This project holds no compiled reference to any of it, which is the whole claim under test.</para>
/// </summary>
public class ImportModuleLayoutTest
{
    private const string ModuleName = "MeshWeaver.Import";

    /// <summary>
    /// The private closure that must ride the prune. Named rather than inferred, for the same
    /// reason the storage gate names its drivers: their presence is the single thing the prune can
    /// take away, and dropping one should be a deliberate edit here rather than a silently
    /// weakened assertion.
    /// </summary>
    public static TheoryData<string> PrivateClosure() =>
    [
        "MeshWeaver.DataStructures.dll",   // IDataSet / IDataTable / IDataRow — the reader contract
        "MeshWeaver.DataSetReader.dll",
        "MeshWeaver.DataSetReader.Csv.dll",
        "MeshWeaver.DataSetReader.Excel.dll",
        "MeshWeaver.DataSetReader.Excel.BinaryFormat.dll",
        "MeshWeaver.DataSetReader.Excel.OpenXmlFormat.dll",
        "MeshWeaver.DataSetReader.Excel.Utils.dll",
        "CsvHelper.dll",
    ];

    [Fact]
    public void ClosureLane_LaysImportOut_UnderModules()
    {
        var expected = Path.Combine(AppContext.BaseDirectory, "modules", ModuleName, ModuleName + ".dll");
        var resolved = MeshBuilder.ResolveModulePath(ModuleName + ".dll");

        // ResolveModulePath falls back to the app folder when modules/<Name>/<Name>.dll is absent,
        // so "the file exists" is NOT the assertion — WHERE it resolved is.
        Assert.True(File.Exists(resolved),
            $"{ModuleName}: the closure lane laid out nothing loadable — ResolveModulePath returned '{resolved}', which does not exist.");
        Assert.Equal(expected, resolved);
    }

    [Theory]
    [MemberData(nameof(PrivateClosure))]
    public void ImportModule_CarriesItsReaderFamily(string assemblyFile)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "modules", ModuleName, assemblyFile);

        Assert.True(File.Exists(path),
            $"'{assemblyFile}' is missing from modules/{ModuleName}/. Nothing in the app closure "
            + "references it, so a deployment importing a spreadsheet would fault at first read. "
            + "Check the closure lane's prune in memex/MeshModulesPublish.targets.");
        Assert.NotNull(Assembly.LoadFrom(path));
    }

    /// <summary>
    /// The seam a portal walks: <c>InstallAssemblies</c> loads the module folder DLL, folds its
    /// <see cref="MeshNodeProviderAttribute"/>, and — the point of this module — records an
    /// <see cref="InstalledModuleAssembly"/> so the in-mesh compile reference set gets it. It
    /// registers no services BY DESIGN: <c>AddImport()</c> is an application-level call a data
    /// source makes for itself, never the host's.
    /// </summary>
    [Fact]
    public void Portal_WithNoCompiledReference_InstallsImportAsAModule()
    {
        var modulePath = MeshBuilder.ResolveModulePath(ModuleName + ".dll");

        var serviceConfigs = new List<Func<IServiceCollection, IServiceCollection>>();
        var builder = new MeshBuilder(configure => serviceConfigs.Add(configure), AddressExtensions.CreateMeshAddress());
        builder.InstallAssemblies(modulePath);
        var services = serviceConfigs.Aggregate(
            (IServiceCollection)new ServiceCollection(), (collection, configure) => configure(collection));

        var installed = Assert.Single(services
            .Where(d => d.ServiceType == typeof(InstalledModuleAssembly))
            .Select(d => (InstalledModuleAssembly)d.ImplementationInstance!));

        // From THAT dll — an app-root copy resolving here would make the assertion true of the
        // wrong bytes, which is precisely the false green the Monolith would give.
        Assert.Equal(modulePath, installed.Assembly.Location);

        // …and the private closure binds FROM THE MODULE FOLDER. ImportRequest.DataSetReaderOptions
        // is typed in MeshWeaver.DataSetReader, which no host references, so resolving this property
        // type is the load a spreadsheet import walks. A missing or mismatched private dependency
        // surfaces here as a loader exception naming what broke — no file, no hub, milliseconds.
        var moduleFolder = Path.GetDirectoryName(modulePath)!;
        var optionsType = installed.Assembly
            .GetType("MeshWeaver.Import.ImportRequest", throwOnError: true)!
            .GetProperty("DataSetReaderOptions")!
            .PropertyType;
        Assert.Equal("MeshWeaver.DataSetReader", optionsType.Assembly.GetName().Name);
        Assert.Equal(moduleFolder, Path.GetDirectoryName(optionsType.Assembly.Location));
    }
}
