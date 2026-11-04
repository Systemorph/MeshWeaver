using System;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🚨 Systemorph/MeshWeaver#6195 (review of #7876) — <b>a compile that runs out of its CPU-lane bound
/// is retried by the type's own watchers until it settles, through every path that can produce
/// one.</b> The unit half (<see cref="LaneExpiryIsNotACompileVerdictTest"/>) pins the predicates;
/// this pins the watchers that consume them, against the real compile service and the real bound.
///
/// <para>The one seam is the emit's own start (<see cref="MeshNodeCompilationService.OnEmitStarting"/>):
/// while <see cref="expireRemaining"/> is positive, the emit holds its lane thread for longer than
/// the bound, exactly as a starved compile does, so the bound expires and cancels it. The bound is
/// not raised anywhere: the slow emit is the subject.</para>
///
/// <list type="number">
/// <item><b>Consecutive expiries.</b> The failed-verdict re-drive's de-duplication used to key on
/// (status, inputs, platform) only. Its filter drops the Pending/Compiling emissions in between, so a
/// retry that expired AGAIN arrived with the same key and was swallowed: the type sat at Unavailable
/// without reaching the retry cap or its give-up line.</item>
/// <item><b>Old build → framework-stale rebuild → expiry.</b> The framework-stale kickoff is one-shot
/// (<c>Take(1)</c>), and a failed rebuild keeps the old coordinates and framework stamp, so the state
/// it leaves is still "stale". The re-drive declined it as the kickoff's case while the kickoff had
/// already unsubscribed: nothing retried it.</item>
/// </list>
/// </summary>
public class ALaneExpiryIsRetriedByTheWatcherTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(2);

    // Emits still to be held past the bound, and emits started — written by the emit thread.
    private int expireRemaining;
    private int emitsStarted;

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
                services.Configure<CompilationCacheOptions>(o => o.RoslynCompileTimeout = Bound));

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private NodeTypeDefinition Definition(MeshNode node) =>
        node.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;

    private void HoldNextEmitsPastTheBound(int count)
    {
        Volatile.Write(ref expireRemaining, count);
        Mesh.ServiceProvider.GetRequiredService<MeshNodeCompilationService>().OnEmitStarting = _ =>
        {
            Interlocked.Increment(ref emitsStarted);
            if (Interlocked.Decrement(ref expireRemaining) >= 0)
                // A starved emit: still working when its bound fires. Bounded, so it always ends.
                SpinWait.SpinUntil(() => false, Bound + TimeSpan.FromSeconds(1));
        };
    }

    private Task<MeshNode> Settled(string typePath, Func<NodeTypeDefinition, bool> predicate, string because) =>
        Mesh.GetMeshNodeStream(typePath)
            .Should().Within(TestTimeouts.CrossSilo)
            .Match(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions) is { } d && predicate(d),
                because, cancellationToken: TestContext.Current.CancellationToken);

    private Task CreateType(string typePath, string contentType, string code) =>
        MeshService.CreateNode(MeshNode.FromPath(typePath) with
            {
                Name = contentType,
                NodeType = MeshNode.NodeTypePath,
                State = MeshNodeState.Active,
                Content = new NodeTypeDefinition { Configuration = $"config => config.WithContentType<{contentType}>()" },
            })
            .SelectMany(_ => MeshService.CreateNode(new MeshNode("api", $"{typePath}/Source")
            {
                NodeType = "Code",
                Name = "api",
                State = MeshNodeState.Active,
                Content = new CodeConfiguration { Language = "csharp", Code = code },
            }))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

    [Fact(Timeout = 300_000)]
    public async Task TwoConsecutiveExpiries_AreBothRetried_AndTheTypeSettlesOk()
    {
        const string TypePath = "type/LaneExpiresTwice";
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        HoldNextEmitsPastTheBound(2);
        try
        {
            await CreateType(TypePath, "LaneExpiresTwiceContent",
                "public record LaneExpiresTwiceContent { public string Title { get; init; } = \"\"; }");

            var settled = Definition(await Settled(TypePath, d => d.CompilationStatus == CompilationStatus.Ok,
                "🚨 the second expiry must be re-driven too — before the fix it was de-duplicated away and the "
                + "type stayed Unavailable (#6195 review)"));
            settled.LatestAssemblyPath.Should().NotBeNullOrEmpty();
            Volatile.Read(ref emitsStarted).Should().BeGreaterThanOrEqualTo(3,
                "two held emits expired and a third compiled — or this test proves nothing");
        }
        finally
        {
            Mesh.ServiceProvider.GetRequiredService<MeshNodeCompilationService>().OnEmitStarting = null;
        }
    }

    [Fact(Timeout = 300_000)]
    public async Task AStaleRebuildThatExpires_IsRetried_AfterTheOneShotKickoffIsSpent()
    {
        const string TypePath = "type/StaleRebuildExpires";
        await CreateType(TypePath, "StaleRebuildExpiresContent",
            "public record StaleRebuildExpiresContent { public string Title { get; init; } = \"\"; }");
        var good = Definition(await Settled(TypePath,
            d => d.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Error, "the first build settles"));
        good.CompilationStatus.Should().Be(CompilationStatus.Ok, $"the source compiles. Error: {good.CompilationError}");

        // A new source, so the rebuild below is a real emit and not a disk-cache hit.
        await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => Mesh.GetMeshNodeStream($"{TypePath}/Source/api").Update(current => current with
            {
                Content = new CodeConfiguration
                {
                    Language = "csharp",
                    Code = "public record StaleRebuildExpiresContent { public string Title { get; init; } = \"\"; public int N { get; init; } }",
                },
            }))
            .Take(1)
            .Should().Within(TestTimeouts.WriteConvergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

        HoldNextEmitsPastTheBound(1);
        try
        {
            // The retained build turns stale on this replica: the one-shot framework-stale kickoff
            // rebuilds it, and that rebuild expires.
            await Mesh.ServiceProvider.GetRequiredService<AccessService>()
                .RunAsSystem(() => Mesh.GetMeshNodeStream(TypePath).Update(current => current with
                {
                    Content = Definition(current) with
                    {
                        CompiledFrameworkVersion = "a-framework-of-another-generation",
                        LastCompiledVersion = 987_654_321,
                    },
                }))
                .Take(1)
                .Should().Within(TestTimeouts.WriteConvergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

            var recovered = Definition(await Settled(TypePath,
                d => d.CompilationStatus == CompilationStatus.Ok
                     && string.Equals(d.CompiledFrameworkVersion, NodeTypeCompilationHelpers.FrameworkVersion, StringComparison.Ordinal),
                "🚨 the expired stale rebuild must be retried once the one-shot kickoff has acted — before the fix "
                + "it was left to a kickoff that had already unsubscribed (#6195 review)"));
            recovered.LatestAssemblyPath.Should().NotBeNullOrEmpty();
            Volatile.Read(ref expireRemaining).Should().BeLessThan(0,
                "the held emit expired and a later one compiled — or this test proves nothing");
        }
        finally
        {
            Mesh.ServiceProvider.GetRequiredService<MeshNodeCompilationService>().OnEmitStarting = null;
        }
    }
}
