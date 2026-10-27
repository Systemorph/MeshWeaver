using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MeshWeaver.Compiler;
using MeshWeaver.ContentCollections;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.NuGet;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// <see cref="NodeSetCompiler.Compile(NodeSet, MeshNode, IReadOnlyList{string}?, IReadOnlyList{string}?, string?, IReadOnlyList{ContentCollectionConfig}?, IReadOnlyList{MetadataReference}, Func{string, string?}, string, string, Func{IReadOnlyList{NuGetPackageReference}, CancellationToken, IReadOnlyList{string}}?, ILogger?, CancellationToken)"/>
/// is a public method of a shipped assembly: its parameter list is its BINARY identity. Adding a
/// trailing optional parameter would replace it, and every already-compiled caller would fail
/// with <see cref="MissingMethodException"/>. The published signature stays, as a forwarder.
/// </summary>
public class NodeSetCompilerPublishedSignatureTest
{
    private static readonly Type[] PublishedParameters =
    [
        typeof(NodeSet), typeof(MeshNode), typeof(IReadOnlyList<string>), typeof(IReadOnlyList<string>),
        typeof(string), typeof(IReadOnlyList<ContentCollectionConfig>), typeof(IReadOnlyList<MetadataReference>),
        typeof(Func<string, string?>), typeof(string), typeof(string),
        typeof(Func<IReadOnlyList<NuGetPackageReference>, CancellationToken, IReadOnlyList<string>>),
        typeof(ILogger), typeof(CancellationToken),
    ];

    [Fact]
    public void ThePublishedCompileSignature_StillExists_WithItsReturnType()
    {
        var method = typeof(NodeSetCompiler).GetMethod(nameof(NodeSetCompiler.Compile), PublishedParameters);
        method.Should().NotBeNull("an already-compiled caller binds exactly this parameter list");
        method!.ReturnType.Should().Be(typeof(NodeSetCompiler.CompiledNode));
        method.GetParameters().Select(p => p.Name).Should().Equal(
            "nodes", "typeNode", "sources", "tests", "configuration", "contentCollections", "references",
            "dependencyIdOf", "toolchainId", "outputDirectory", "resolveNuGet", "logger", "ct");
    }

    [Fact]
    public void NegativeControl_TheSignatureWithATrailingModuleParameter_IsNotPublished()
    {
        // The shape the review caught: the published list plus a trailing module parameter. It must
        // not exist, or it would be the ONLY signature an old caller could not find.
        typeof(NodeSetCompiler).GetMethod(nameof(NodeSetCompiler.Compile),
                [.. PublishedParameters, typeof(IReadOnlyCollection<string>)])
            .Should().BeNull();
    }

    [Fact(Timeout = 120_000)]
    public void APositionalCallEndingInANullResolver_StillBindsThePublishedSignature()
    {
        // SOURCE compatibility: `outputDirectory, null` positionally. With the module-aware compile
        // as an OVERLOAD whose extra parameter sits where resolveNuGet sits, this line did not
        // compile (CS0121, ambiguous). That this file compiles is the regression; the call runs too.
        var ct = TestContext.Current.CancellationToken;
        var output = Path.Combine(Path.GetTempPath(), "mw-published-pos-" + Guid.NewGuid().ToString("N"));
        var (type, code) = Nodes("Pos");
        try
        {
            var compiled = NodeSetCompiler.Compile(
                NodeSet.Create([type, code]), type, null, null, null, null, CompileReferences.Default,
                _ => null, NodeTypeCompilationHelpers.ProcessToolchainId, output, null);
            compiled.DependencyEdges.Should().NotBeNull();
            ct.IsCancellationRequested.Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(output, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void TheModuleAwareCompile_IsADistinctName_NotAnOverloadOfThePublishedOne()
    {
        typeof(NodeSetCompiler).GetMethods().Where(m => m.Name == nameof(NodeSetCompiler.Compile))
            .Should().ContainSingle("one Compile, the published one — a second overload is what made positional calls ambiguous");
        typeof(NodeSetCompiler).GetMethod(nameof(NodeSetCompiler.CompileWithModules)).Should().NotBeNull();
    }

    private static (MeshNode Type, MeshNode Code) Nodes(string id) => (
        MeshNode.FromPath($"App/{id}") with
        {
            Name = id, NodeType = MeshNode.NodeTypePath, State = MeshNodeState.Active,
            LastModified = DateTimeOffset.UtcNow, Content = new NodeTypeDefinition(),
        },
        new MeshNode("Own", $"App/{id}/Source")
        {
            NodeType = "Code", Name = "Own", State = MeshNodeState.Active, LastModified = DateTimeOffset.UtcNow,
            Content = new CodeConfiguration { Language = "csharp", Code = $"/// <summary>Own.</summary>\npublic record {id}Own(int V);" },
        });

    [Fact(Timeout = 120_000)]
    public void ACallThroughThePublishedSignature_Compiles_AndRecordsTheEdgeSet()
    {
        var ct = TestContext.Current.CancellationToken;
        var output = Path.Combine(Path.GetTempPath(), "mw-published-sig-" + Guid.NewGuid().ToString("N"));
        var type = MeshNode.FromPath("App/Sig") with
        {
            Name = "Sig", NodeType = MeshNode.NodeTypePath, State = MeshNodeState.Active,
            LastModified = DateTimeOffset.UtcNow, Content = new NodeTypeDefinition(),
        };
        var code = new MeshNode("Own", "App/Sig/Source")
        {
            NodeType = "Code", Name = "Own", State = MeshNodeState.Active, LastModified = DateTimeOffset.UtcNow,
            Content = new CodeConfiguration { Language = "csharp", Code = "/// <summary>Own.</summary>\npublic record SigOwn(int V);" },
        };
        try
        {
            // Invoked through reflection on the exact published parameter list — how a caller
            // compiled against the earlier assembly binds it.
            var method = typeof(NodeSetCompiler).GetMethod(nameof(NodeSetCompiler.Compile), PublishedParameters)!;
            var compiled = (NodeSetCompiler.CompiledNode)method.Invoke(null,
            [
                NodeSet.Create([type, code]), type, null, null, null, null, CompileReferences.Default,
                (Func<string, string?>)(_ => null), NodeTypeCompilationHelpers.ProcessToolchainId, output,
                null, null, ct,
            ])!;
            compiled.DependencyEdges.Should().NotBeNull("the forwarder runs the same compile");
            compiled.DependencyEdges!.Edges.Should().BeEmpty("no module files are named, so no Module edges");
        }
        finally
        {
            try { Directory.Delete(output, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
