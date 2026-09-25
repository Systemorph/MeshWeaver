using System;
using System.Collections.Generic;
using MeshWeaver.Compiler;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins <see cref="NodeCompileShaping.ResolveCodeIncludesInMemory"/> — the entry point the tree
/// bake (<see cref="NodeSetCompiler.ResolveInputs"/>) uses instead of driving the reactive
/// <c>@@</c>-include walk with Rx's blocking <c>.Wait()</c>.
///
/// <para>The property that matters is that nothing BLOCKS: every read runs on the calling thread,
/// inside <c>Subscribe</c>, and the substituted text comes back from the same call. A walk that hopped
/// a thread would have needed a park to collect its answer — which is exactly the shape that becomes
/// a deadlock once the caller is a single-threaded scheduler.</para>
/// </summary>
public class InMemoryIncludeWalkTest
{
    private const string TypePath = "Widget/Thing";

    private static MeshNode Code(string path, string code) =>
        new(path[(path.LastIndexOf('/') + 1)..], path[..path.LastIndexOf('/')])
        {
            NodeType = "Code",
            State = MeshNodeState.Active,
            Content = new CodeConfiguration { Code = code, Language = "csharp" },
        };

    [Fact]
    public void NestedIncludes_ResolveOnTheCallingThread_AndRecordTheClosure()
    {
        var nodes = new Dictionary<string, MeshNode>
        {
            ["Widget/Snippets/Outer"] = Code("Widget/Snippets/Outer", "// outer\n@@Widget/Snippets/Inner"),
            ["Widget/Snippets/Inner"] = Code("Widget/Snippets/Inner", "// inner"),
        };
        var callingThread = Environment.CurrentManagedThreadId;
        var readThreads = new List<int>();
        var closure = new Dictionary<string, string>(StringComparer.Ordinal);

        var resolved = NodeCompileShaping.ResolveCodeIncludesInMemory(
            "public record Thing;\n@@Widget/Snippets/Outer",
            TypePath,
            (anchored, authored) =>
            {
                readThreads.Add(Environment.CurrentManagedThreadId);
                if (nodes.TryGetValue(anchored, out var hit))
                    return (hit, anchored);
                if (authored is not null && nodes.TryGetValue(authored, out var fallback))
                    return (fallback, authored);
                return (null, anchored);
            },
            NullLogger.Instance,
            closure);

        Assert.Equal("public record Thing;\n// outer\n// inner", resolved);
        // Every include read runs inline, on the caller's thread — no hop, so nothing to park on.
        Assert.Equal(2, readThreads.Count);
        Assert.All(readThreads, id => Assert.Equal(callingThread, id));
        Assert.Equal(2, closure.Count);
        Assert.Equal("// outer\n@@Widget/Snippets/Inner", closure["Widget/Snippets/Outer"]);
        Assert.Equal("// inner", closure["Widget/Snippets/Inner"]);
    }

    [Fact]
    public void AnUnresolvedInclude_StaysVerbatim()
    {
        var resolved = NodeCompileShaping.ResolveCodeIncludesInMemory(
            "public record Thing;\n@@Widget/Snippets/Missing",
            TypePath,
            (anchored, _) => (null, anchored),
            NullLogger.Instance);

        Assert.Equal("public record Thing;\n@@Widget/Snippets/Missing", resolved);
    }

    /// <summary>
    /// A fault in the read surfaces as the ORIGINAL exception from the call — not an
    /// <see cref="AggregateException"/> (what a Task bridge wraps it in) and not a hang.
    /// </summary>
    [Fact]
    public void AFaultingRead_ThrowsTheOriginalException()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            NodeCompileShaping.ResolveCodeIncludesInMemory(
                "@@Widget/Snippets/Broken",
                TypePath,
                (_, _) => throw new InvalidOperationException("read failed"),
                NullLogger.Instance));

        Assert.Equal("read failed", error.Message);
    }
}
