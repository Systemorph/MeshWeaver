using System.Collections.Immutable;
using System.Reflection;
using System.Reactive.Linq;
using MeshWeaver.Compiler;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// An artifact's completion time cannot prove that it contains an edit made after its source
/// snapshot. Exercises the real compiler and emitted methods, including the test registration
/// source, without a sleep, mocked compiler, or altered file timestamp.
/// </summary>
public class CompileCacheInputFreshnessTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // The real fixture shares disk cache per test class; each case owns a distinct artifact.
    private readonly string typePath = "type/CompileCacheInputFreshness" + Guid.NewGuid().ToString("N");
    private IMeshNodeCompilationService Compiler =>
        Mesh.ServiceProvider.GetRequiredService<IMeshNodeCompilationService>();

    private MeshNode TypeNode(DateTimeOffset modified, string marker = "original") => MeshNode.FromPath(typePath) with
    {
        NodeType = MeshNode.NodeTypePath,
        Name = "Compile cache input freshness",
        LastModified = modified,
        Content = new NodeTypeDefinition
        {
            Configuration = $"config => config.WithContentType<CacheFreshnessContent>().Set(\"{marker}\", \"cache-input-probe\")",
        },
    };

    private ImmutableArray<MeshNode> Sources(DateTimeOffset modified, int answer, string testCase) =>
    [
        new MeshNode("Probe", typePath + "/Source")
        {
            NodeType = "Code", LastModified = modified,
            Content = new CodeConfiguration
            {
                Language = "csharp",
                Code = $$"""
                    public record CacheFreshnessContent { public string Title { get; init; } = ""; }
                    public static class CacheFreshnessApi { public static int Answer() => {{answer}}; }
                    """,
            },
        },
        new MeshNode("Registration", typePath + "/Test")
        {
            NodeType = "Code", LastModified = modified,
            Content = new CodeConfiguration
            {
                Language = "csharp",
                Code = $$"""
                    public static class CacheFreshnessRegistration
                    {
                        public static string RegisteredCase() => "{{testCase}}";
                    }
                    """,
            },
        },
    ];

    private static string Transcript(NodeCompilationResult result) =>
        string.Join(" | ", result.Log?.Messages.Select(m => m.Message) ?? []);

    private string EmittedBehavior(NodeCompilationResult result)
    {
        var cache = Mesh.ServiceProvider.GetRequiredService<ICompilationCacheService>();
        using var pinned = cache.PinForScan(cache.SanitizeNodeName(typePath), result.AssemblyLocation);
        var assembly = Assert.IsAssignableFrom<Assembly>(pinned.Context.LoadNodeAssembly());
        var api = Assert.IsAssignableFrom<Type>(assembly.GetType("CacheFreshnessApi"));
        var registration = Assert.IsAssignableFrom<Type>(assembly.GetType("CacheFreshnessRegistration"));
        return $"{api.GetMethod("Answer")!.Invoke(null, null)}|"
            + registration.GetMethod("RegisteredCase")!.Invoke(null, null);
    }

    [Theory(Timeout = 300_000)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SourceAndTestEditsBeforeThePreviousEmitFinished_MustNotReuseItsBytes(
        bool changeSource, bool changeTest)
    {
        var capturedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var type = TypeNode(capturedAt);
        var before = Sources(capturedAt, 42, "original-case");
        var first = Assert.IsAssignableFrom<NodeCompilationResult>(await Compiler.CompileAndGetConfigurations(type, before)
            .Take(1).Should().Within(TestTimeouts.Convergence).Emit());
        Transcript(first).Should().Contain("Compiled assembly written to");
        EmittedBehavior(first).Should().Be("42|original-case");

        // Model an edit that landed after the immutable snapshot was captured, but before its
        // DLL finished writing. Control the snapshot timestamp from that actual artifact; never
        // change the artifact's clock, sleep, or race a fast machine against wall time.
        var finishedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(first.AssemblyLocation!), TimeSpan.Zero);
        var editedAt = finishedAt.AddTicks(-1);
        editedAt.Should().BeAfter(capturedAt);
        var answer = changeSource ? 43 : 42;
        var testCase = changeTest ? "new-case" : "original-case";
        var changed = Sources(editedAt, answer, testCase);
        var after = ImmutableArray.Create(changeSource ? changed[0] : before[0],
            changeTest ? changed[1] : before[1]);
        var second = Assert.IsAssignableFrom<NodeCompilationResult>(await Compiler.CompileAndGetConfigurations(type, after)
            .Take(1).Should().Within(TestTimeouts.Convergence).Emit());
        var behavior = EmittedBehavior(second);
        var stampedCurrentSources = after.All(source => second.CompiledSources is { } snapshot
            && snapshot.TryGetValue(source.Path, out var stamp)
            && stamp == NodeTypeDefinition.SourceVersionOf(source));
        Output.WriteLine("First DLL finished {0:O}; edit {1:O}; second artifact {2}; emitted={3}; stampsCurrent={4}",
            finishedAt, editedAt, second.AssemblyLocation ?? "(none)", behavior, stampedCurrentSources);
        Output.WriteLine("Second compiler transcript: {0}", Transcript(second));

        // Before the fix this says 42|original-case while claiming the new source stamps. The
        // failure names BOTH the stale behavior and the false provenance, not only a path choice.
        Assert.Equal($"{answer}|{testCase};stampsCurrent=True", $"{behavior};stampsCurrent={stampedCurrentSources}");
        second.AssemblyLocation.Should().NotBe(first.AssemblyLocation);
        second.CompiledDependencies![CompiledDependencies.ContentKey]
            .Should().NotBe(first.CompiledDependencies![CompiledDependencies.ContentKey]);
    }

    private string? EmittedConfiguration(NodeCompilationResult result)
    {
        var configure = Assert.Single(result.NodeTypeConfigurations).HubConfiguration;
        Assert.NotNull(configure);
        return configure(new MessageHubConfiguration(Mesh.ServiceProvider, Mesh.Address))
            .Get<string>("cache-input-probe");
    }

    [Fact(Timeout = 300_000)]
    public async Task ConfigurationEditBeforeThePreviousEmitFinished_MustNotReuseItsConfiguration()
    {
        var capturedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var before = TypeNode(capturedAt);
        var sources = Sources(capturedAt, 42, "original-case");
        var first = Assert.IsAssignableFrom<NodeCompilationResult>(await Compiler
            .CompileAndGetConfigurations(before, sources).Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit());
        Transcript(first).Should().Contain("Compiled assembly written to");
        EmittedConfiguration(first).Should().Be("original");
        var editedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(first.AssemblyLocation!), TimeSpan.Zero)
            .AddTicks(-1);
        editedAt.Should().BeAfter(capturedAt);

        var second = Assert.IsAssignableFrom<NodeCompilationResult>(await Compiler
            .CompileAndGetConfigurations(TypeNode(editedAt, "changed"), sources).Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit());
        EmittedConfiguration(second).Should().Be("changed");
        EmittedBehavior(second).Should().Be("42|original-case", "only the configuration changed");
        second.AssemblyLocation.Should().NotBe(first.AssemblyLocation);
        second.CompiledDependencies![CompiledDependencies.ContentKey]
            .Should().NotBe(first.CompiledDependencies![CompiledDependencies.ContentKey]);
    }

    [Theory(Timeout = 300_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADefinitionWithNullConfiguration_CanMatchButAnUnresolvedDefinitionCannot(
        bool definitionBecomesUnresolved)
    {
        var capturedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        // The first call has an explicit definition. The second can resolve neither a definition
        // in Content nor this deliberately absent owning type. Keep every other generated input
        // unchanged so the old null-as-empty path would certify exactly the first artifact.
        var before = TypeNode(capturedAt) with
        {
            NodeType = "type/AbsentDefinition" + Guid.NewGuid().ToString("N"),
            Content = new NodeTypeDefinition(),
        };
        var sources = Sources(capturedAt, 42, "original-case");
        var first = Assert.IsAssignableFrom<NodeCompilationResult>(await Compiler
            .CompileAndGetConfigurations(before, sources).Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit());
        Transcript(first).Should().Contain("Compiled assembly written to");
        EmittedBehavior(first).Should().Be("42|original-case");

        var next = definitionBecomesUnresolved ? before with { Content = null } : before;
        var second = Assert.IsAssignableFrom<NodeCompilationResult>(await Compiler
            .CompileAndGetConfigurations(next, sources).Take(1)
            .Should().Within(TestTimeouts.Convergence).Emit());
        Output.WriteLine("Second compiler transcript: {0}", Transcript(second));
        EmittedBehavior(second).Should().Be("42|original-case");
        if (definitionBecomesUnresolved)
        {
            Transcript(second).Should().Contain("NULL — the read stalled or the node is absent");
            Transcript(second).Should().NotContain("Cache hit",
                "an unestablished definition cannot certify equality with the producing input");
            second.AssemblyLocation.Should().NotBe(first.AssemblyLocation);
        }
        else
        {
            Transcript(second).Should().Contain("Cache hit",
                "a present definition with a legitimately null Configuration is conclusive");
            second.AssemblyLocation.Should().Be(first.AssemblyLocation);
            second.CompiledDependencies![CompiledDependencies.ContentKey]
                .Should().Be(first.CompiledDependencies![CompiledDependencies.ContentKey]);
        }
    }

}
