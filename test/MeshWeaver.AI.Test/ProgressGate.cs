#pragma warning disable CS1591

using System;
using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Reactive.Assertions;

namespace MeshWeaver.AI.Test;

/// <summary>
/// A happens-before for tests that assert a subscriber observes script progress INCREMENTALLY.
///
/// <para>🚨 Such a test is racy by construction unless the subscriber is provably attached before
/// the script logs anything, and "attached" is not something a test may assume.
/// <c>GetMeshNodeStream</c>'s first emission is the OWNER's snapshot AT SUBSCRIBE TIME, so a
/// subscription that lands after the run has finished correctly sees ONE terminal snapshot
/// carrying the whole history — no batching anywhere, and an assertion counting snapshots reads
/// that as a framework defect. #2421 is the recorded instance: a ~350 ms run against a ~914 ms
/// subscribe under CI contention, reported as <c>Observed: [4@914ms], but found 1</c>.</para>
///
/// <para>The gate closes that by inverting the order: the script BLOCKS on a node until the test
/// — having seen its own first snapshot of the activity — flips it. Waiting is reactive
/// (<c>GetMeshNodeStream(...).Where(...).FirstAsync()</c>), never a poll or a sleep, and the
/// open/shut literals live here so the script text and the test's flip cannot drift apart.</para>
/// </summary>
internal static class ProgressGate
{
    /// <summary><c>ExecuteScriptRequest.Inputs</c> key carrying the gate node's path.</summary>
    private const string InputKey = "gate";

    /// <summary><see cref="MeshNode.Name"/> of a gate that has been released.</summary>
    private const string OpenMarker = "go";

    /// <summary>
    /// Prepend to a script body (it opens with <c>using</c> directives, which C# scripting requires
    /// before any statement). Blocks the run until <see cref="Release"/> flips the gate node.
    /// </summary>
    public const string ScriptPrologue = $$"""
        using System.Reactive.Threading.Tasks;
        // Wait for the test's go-ahead. It flips this node only after its subscription to the
        // activity has produced a first snapshot — so everything logged below is guaranteed to
        // happen with a live subscriber attached.
        await Mesh.GetMeshNodeStream(Inputs["{{InputKey}}"].GetString()!)
            .Where(node => node is not null && node.Name == "{{OpenMarker}}")
            .FirstAsync().ToTask(Ct);

        """;

    /// <summary>Creates a shut gate node in <paramref name="partition"/> and returns its path.</summary>
    public static async Task<string> Seed(IMeshService mesh, string partition)
    {
        var id = $"gate-{Guid.NewGuid():N}";
        // Plain node, no content — Name alone carries the open/shut bit.
        await mesh.CreateNode(new MeshNode(id, partition) { Name = "wait", NodeType = "Markdown" })
            .Should().Within(TimeSpan.FromSeconds(30)).Emit();
        return $"{partition}/{id}";
    }

    /// <summary>The <c>Inputs</c> payload that tells the script which gate to wait on.</summary>
    public static ImmutableDictionary<string, JsonElement> Inputs(string gatePath)
        => ImmutableDictionary<string, JsonElement>.Empty
            .Add(InputKey, JsonSerializer.SerializeToElement(gatePath));

    /// <summary>Opens the gate. Cold — the caller subscribes (typically by awaiting the assertion).</summary>
    public static IObservable<MeshNode> Release(IWorkspace workspace, string gatePath)
        => workspace.GetMeshNodeStream(gatePath).Update(node => node with { Name = OpenMarker });
}
