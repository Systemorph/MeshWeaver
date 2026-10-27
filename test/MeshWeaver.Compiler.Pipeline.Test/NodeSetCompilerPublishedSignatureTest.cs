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
