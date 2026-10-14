using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Compiler;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 <b>A compile never reports a verdict over a source set OLDER than the versions the type's
/// own record names</b> — MeshWeaver#6631.
///
/// <para>Measured on memex.systemorph.com 2026-10-06: <c>Hosting/Deployment</c> was compiled on
/// the activation path and again by the batch bake over a set mixing the NEW
/// <c>Source/PrBabysitterTending</c> (<c>public static partial class PrBabysitter</c>) with the
/// OLD, non-partial <c>Source/PrBabysitter</c>, although the type's
/// <see cref="NodeTypeDefinition.CurrentSourceVersions"/> had named the new <c>PrBabysitter</c> for
/// 50 s. Roslyn answered <c>CS0260 Missing partial modifier</c> plus ~90 follow-ons, and the type
/// was stamped with a compile verdict about code that was fine.</para>
///
/// <para>This reproduces that set deterministically — the stale view is handed in as the snapshot
/// — and pins the rule: such a set is UNESTABLISHED (<see cref="SourceDiscoveryUnavailableException"/>
/// → <c>CompilationStatus.Unavailable</c>, "not evaluated"), never a Roslyn verdict. The negative
/// control is the same type over the set the record names, which compiles.</para>
/// </summary>
public class ACompileNeverReadsASetOlderThanItsRecordTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private const string TypePath = "type/StaleSourceSetProbe";
    private const string SourceNs = TypePath + "/Source";

    private static readonly DateTimeOffset Old = new(2026, 10, 6, 6, 40, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset TendingWritten = new(2026, 10, 6, 6, 53, 18, TimeSpan.Zero);
    private static readonly DateTimeOffset NewWritten = new(2026, 10, 6, 6, 53, 30, TimeSpan.Zero);

    private IMeshNodeCompilationService Compiler =>
        Mesh.ServiceProvider.GetRequiredService<IMeshNodeCompilationService>();

    private static MeshNode Code(string id, DateTimeOffset written, string code) =>
        new(id, SourceNs)
        {
            NodeType = "Code",
            Name = id,
            State = MeshNodeState.Active,
            LastModified = written,
            Content = new CodeConfiguration { Language = "csharp", Code = code },
        };

    /// <summary>The OLD <c>PrBabysitter</c>: not partial — what the stale view still held.</summary>
    private static MeshNode OldBabysitter() => Code("PrBabysitter", Old, """
        public record StaleSourceSetProbeContent { public string? Name { get; init; } }
        public static class PrBabysitter { public static int Report() => 1; }
        """);

    /// <summary>The NEW <c>PrBabysitter</c>: partial — what the store and the record hold.</summary>
    private static MeshNode NewBabysitter() => Code("PrBabysitter", NewWritten, """
        public record StaleSourceSetProbeContent { public string? Name { get; init; } }
        public static partial class PrBabysitter { public static int Report() => 1; }
        """);

    private static MeshNode Tending() => Code("PrBabysitterTending", TendingWritten, """
        public static partial class PrBabysitter { public static int Tend() => Report() + 1; }
        """);

    /// <summary>The type, with the record its sources watcher published once the store held the
    /// NEW <c>PrBabysitter</c> and the new <c>PrBabysitterTending</c>.</summary>
    private static MeshNode TypeNode() => MeshNode.FromPath(TypePath) with
    {
        Name = "StaleSourceSetProbe",
        NodeType = MeshNode.NodeTypePath,
        State = MeshNodeState.Active,
        LastModified = NewWritten,
        Content = new NodeTypeDefinition
        {
            Description = "a type whose record names a newer source than the view that answers",
            Configuration = "config => config.WithContentType<StaleSourceSetProbeContent>()",
            CurrentSourceVersions = ImmutableDictionary<string, long>.Empty
                .Add($"{SourceNs}/PrBabysitter", NodeTypeDefinition.SourceVersionOf(NewBabysitter()))
                .Add($"{SourceNs}/PrBabysitterTending", NodeTypeDefinition.SourceVersionOf(Tending())),
        },
    };

    [Fact(Timeout = 300_000)]
    public async Task ASetOlderThanTheRecord_IsNotEvaluated_NeverACompileVerdict()
    {
        var staleView = new[] { OldBabysitter(), Tending() };

        var outcome = await Compiler.CompileAndGetConfigurations(TypeNode(), staleView)
            .Materialize()
            .Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the compile must settle one way or the other",
                cancellationToken: TestContext.Current.CancellationToken);

        // Without the rule this is an OnNext carrying a failed result with CS0260 — a verdict
        // about the author's code, over a set the store had already moved past.
        outcome.Kind.Should().Be(NotificationKind.OnError,
            "a set holding PrBabysitter at an earlier version than the type's own record names is "
            + $"a stale view, not the source set — got {Describe(outcome)}");
        outcome.Exception.Should().BeOfType<SourceDiscoveryUnavailableException>(
            "the refusal must classify as UNESTABLISHED (CompilationStatus.Unavailable, PreWarm "
            + "'not evaluated'), never as CompilationStatus.Error");
        outcome.Exception!.Message.Should().Contain($"{SourceNs}/PrBabysitter",
            "the refusal names the source that is behind the record");
    }

    /// <summary>Negative control: the set the record names compiles — the rule refuses only a
    /// view that is provably BEHIND the record.</summary>
    [Fact(Timeout = 300_000)]
    public async Task TheSetTheRecordNames_Compiles()
    {
        var current = new[] { NewBabysitter(), Tending() };

        var outcome = await Compiler.CompileAndGetConfigurations(TypeNode(), current)
            .Materialize()
            .Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the compile must settle", cancellationToken: TestContext.Current.CancellationToken);

        outcome.Kind.Should().Be(NotificationKind.OnNext, Describe(outcome));
        outcome.Value.Should().NotBeNull();
        outcome.Value!.AssemblyLocation.Should().NotBeNullOrEmpty(
            "the partial PrBabysitter and its partial sibling compile together");
    }

    /// <summary>A set NEWER than the record is the ordinary watcher lag (the record trails the
    /// files) and must still compile — the rule is one-sided.</summary>
    [Fact(Timeout = 300_000)]
    public async Task ASetNewerThanTheRecord_StillCompiles()
    {
        var newer = NewBabysitter() with { LastModified = NewWritten.AddMinutes(1) };

        var outcome = await Compiler.CompileAndGetConfigurations(TypeNode(), [newer, Tending()])
            .Materialize()
            .Take(1)
            .Should().Within(TestTimeouts.Convergence)
            .Emit("the compile must settle", cancellationToken: TestContext.Current.CancellationToken);

        outcome.Kind.Should().Be(NotificationKind.OnNext, Describe(outcome));
        outcome.Value!.AssemblyLocation.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// The race the activation path runs: the cached synced query replays SYNCHRONOUSLY and used to
    /// win instantly whatever it held. Held to the record, a stale cached answer no longer wins —
    /// the slower authoritative read does.
    /// </summary>
    [Fact]
    public void AStaleCachedAnswer_LosesTheRaceToTheAuthoritativeRead()
    {
        var record = ((NodeTypeDefinition)TypeNode().Content!).CurrentSourceVersions;
        var probe = new Subject<SourceSnapshot>();
        var cached = Observable.Return(SourceSnapshot.Established(new[] { OldBabysitter(), Tending() }))
            .Select(s => SourceSetFreshness.RefuseIfOlderThanRecord(s, record));

        var won = new List<SourceSnapshot>();
        using var _ = NodeCompileShaping.RaceSourceSnapshot(
                probe.Select(s => SourceSetFreshness.RefuseIfOlderThanRecord(s, record)), cached)
            .Subscribe(won.Add);

        won.Should().BeEmpty("the stale cached answer must not settle the race");

        probe.OnNext(SourceSnapshot.Established(new[] { NewBabysitter(), Tending() }));
        probe.OnCompleted();

        won.Should().ContainSingle().Which.IsEstablished.Should().BeTrue();
        won[0].Sources.Single(n => n.Id == "PrBabysitter").LastModified.Should().Be(NewWritten,
            "the authoritative read's current set is the one compiled");
    }

    private static string Describe(Notification<NodeCompilationResult?> n) => n.Kind switch
    {
        NotificationKind.OnError => $"error {n.Exception!.GetType().Name}: {n.Exception.Message}",
        NotificationKind.OnNext => n.Value is null
            ? "a null result"
            : $"result location='{n.Value.AssemblyLocation}' diagnostics=["
              + string.Join("; ", (n.Value.Diagnostics ?? []).Take(5).Select(d => $"{d.Id} {d.Message}"))
              + "]",
        _ => "completion with no value",
    };
}
