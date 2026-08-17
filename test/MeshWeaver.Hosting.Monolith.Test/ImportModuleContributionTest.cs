#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MeshWeaver.Compiler;
using MeshWeaver.Import;
using MeshWeaver.Import.Configuration;
using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// Pins the import stack's module shape. Import is the module that <b>registers nothing</b>: no
/// host ever called <c>AddImport()</c> — that is an application-level call a data source makes for
/// itself — and both portals referenced the assembly for exactly one reason, so that IN-MESH source
/// could <c>using MeshWeaver.Import</c>.
///
/// <para>So the thing that has to hold is not a service descriptor: it is that listing the DLL puts
/// it on the <b>in-mesh compile reference set</b>, and that the types in-mesh content actually uses
/// compile with the module's ENTRY assembly alone — a module's private closure (here the five
/// <c>MeshWeaver.DataSetReader.*</c> assemblies) resolves at runtime from the module folder but is
/// NOT a metadata reference. Both are asserted below, the second by compiling the real shape from
/// <c>Cornerstone/Pricing</c>'s <c>MicrosoftSampleData</c> node source.</para>
/// </summary>
public class ImportModuleContributionTest
{
    [Fact]
    public void TheAssembly_CarriesTheMeshModuleAttribute_AndRegistersNothing()
    {
        var assembly = typeof(ImportMeshModuleAttribute).Assembly;

        var meshHalf = Assert.Single(assembly.GetCustomAttributes<MeshNodeProviderAttribute>());
        var node = Assert.Single(meshHalf.Nodes);
        Assert.Equal("ModuleDefinition", node.NodeType);
        // Deliberately empty: AddImport() is the application's call, never the host's.
        Assert.Empty(node.GlobalServiceConfigurations);
        Assert.Empty(meshHalf.BuilderConfigurations);

        // No endpoint half — the import stack publishes no HTTP surface, and this assembly does
        // not reference the ASP.NET hook at all (matched by name so it stays reference-free).
        Assert.DoesNotContain(
            assembly.GetCustomAttributes(),
            a => a.GetType().BaseType?.Name == "MeshEndpointProviderAttribute");
    }

    [Fact]
    public void InstalledModule_JoinsTheInMeshCompileReferenceSet()
    {
        var module = new InstalledModuleAssembly(typeof(ImportMeshModuleAttribute).Assembly);

        var composed = CompileReferences.ComposeWithModules([module]);

        Assert.Contains(composed, r => string.Equals(
            r.Display, module.Assembly.Location, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 🚨 The falsification that matters. `ComposeWithModules` adds the module's ENTRY assembly
    /// only, so if <c>ExcelImportConfiguration</c> (or anything on its base chain) exposed a
    /// <c>MeshWeaver.DataSetReader</c> type, in-mesh source would fail CS0012 in exactly the
    /// deployment where Import is module-only — and NOT in the dev Monolith, which still carries
    /// Import in its app closure through the Northwind sample. That asymmetry makes a portal
    /// boot-smoke a false green here, so the claim is pinned as a compile instead: the real node
    /// source shape, against corelib + MeshWeaver.Import.dll and nothing else from the import stack.
    /// </summary>
    [Fact]
    public void InMeshSource_CompilesAgainstTheEntryAssemblyAlone()
    {
        // The shape of samples/Graph/Data/Cornerstone/Pricing/Source/MicrosoftSampleData.cs.
        const string source = """
            using System.Collections.Generic;
            using MeshWeaver.Import.Configuration;

            public static class InMeshSample
            {
                public static readonly ExcelImportConfiguration[] ImportConfigs =
                [
                    new()
                    {
                        Name = "Microsoft.xlsx",
                        Address = "pricing/Microsoft/2026",
                        TypeName = "PropertyRisk",
                        DataStartRow = 7,
                        TotalRowMarkers = new HashSet<string> { "Total", "Grand Total" },
                        TotalRowScanAllCells = true,
                        TotalRowMatchExact = false,
                        Mappings =
                        [
                            new() { TargetProperty = "Id", Kind = MappingKind.Direct, SourceColumns = ["C"] },
                            new() { TargetProperty = "PricingId", Kind = MappingKind.Constant, ConstantValue = "2026" },
                            new() { TargetProperty = "TsiContent", Kind = MappingKind.Sum, SourceColumns = ["G", "I"] }
                        ],
                        Allocations = [new() { TargetProperty = "TsiBi", WeightColumns = ["Q"] }],
                        IgnoreRowExpressions = ["Id == null"]
                    }
                ];
            }
            """;

        // corelib + the framework facades the SDK splits System.* across, then the module's entry
        // assembly — and NOTHING from its private DataSetReader closure.
        var coreDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(coreDir, "System.Runtime.dll")),
            MetadataReference.CreateFromFile(Path.Combine(coreDir, "System.Collections.dll")),
            MetadataReference.CreateFromFile(Path.Combine(coreDir, "System.ComponentModel.Annotations.dll")),
            MetadataReference.CreateFromFile(typeof(ExcelImportConfiguration).Assembly.Location),
        };

        var compilation = CSharpCompilation.Create(
            "InMeshSampleCompile",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var errors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();

        Assert.True(errors.Count == 0,
            "In-mesh source must compile with the Import module's entry assembly alone — a CS0012 "
            + "here means a MeshWeaver.DataSetReader type leaked into the public surface in-mesh "
            + "content uses, which would break every deployment where Import is module-only:\n"
            + string.Join("\n", errors));
    }
}
