using System;
using System.Reactive.Linq;
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
/// 🚨 <b>A boot adoption in flight — or one this process recorded but whose stamp is still held —
/// wins over the framework-stale kickoff.</b>
///
/// <para><b>The incident</b> (the dev instance's first platform-4 pod after #8429). The boot bundle
/// source adopted the Essentials package BEFORE the sweep, as intended, and the seeder activated
/// each type's hub to stamp it. Every record still named the previous platform's framework, so the
/// activation armed the framework-stale kickoff, which — unlike the first-build kickoff (#1763) —
/// never consulted the adoption reservation: it flipped <c>Essentials/ActionLink</c> to Pending, the
/// untracked-module gate parked it ("no prebuilt build for it landed") 78 ms before the adoption it
/// was racing logged ADOPTED, the park's write outranked the adoption in the sweep's overlay, a type
/// sharing its sources read as a regression, readiness was refused and the publication gate
/// discarded the adoption stamps that would have healed it.</para>
///
/// <para><b>The run.</b> The race is reproduced in ORDER, deterministically, through the one seam the
/// seeder itself uses — <see cref="NodeTypeAdoptionRegistry"/>: the reservation is taken first, the
/// record turns framework-stale while it is held (the seeder's activation), and only then does the
/// adoption finish. The two outcomes the seeder can have are both pinned: a PROVISIONAL adoption
/// (recorded in the ledger, shared stamp held) must leave the record alone; a DECLINED adoption must
/// still rebuild once the reservation clears — the interlock delays, it never cancels.</para>
/// </summary>
public class AnInFlightAdoptionWinsOverTheFrameworkStaleKickoffTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string OtherFramework = "a-framework-of-the-previous-platform";

    /// <summary>A window long enough for the unguarded kickoff to flip (it does so within
    /// milliseconds of the stale write) and short enough to pay in full on every passing run.</summary>
    private static readonly TimeSpan Silence = TimeSpan.FromSeconds(8);

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private NodeTypeAdoptionRegistry Adoptions => Mesh.ServiceProvider.GetRequiredService<NodeTypeAdoptionRegistry>();

    private NodeTypeDefinition Definition(MeshNode node) =>
        node.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)!;

    private Task<MeshNode> Settled(string typePath, Func<NodeTypeDefinition, bool> predicate, string because) =>
        Mesh.GetMeshNodeStream(typePath)
            .Should().Within(TestTimeouts.CrossSilo)
            .Match(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions) is { } d && predicate(d),
                because, cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>What the unguarded kickoff does to the record: flips it, or has already rebuilt it.</summary>
    private IObservable<MeshNode> KickedOrRebuilt(string typePath) =>
        Mesh.GetMeshNodeStream(typePath)
            .Where(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions) is { } d
                        && (d.CompilationStatus is CompilationStatus.Pending or CompilationStatus.Compiling
                            || string.Equals(d.CompiledFrameworkVersion, NodeTypeCompilationHelpers.FrameworkVersion,
                                StringComparison.Ordinal)))
            .Select(n => n!);

    private async Task<NodeTypeDefinition> BuiltType(string typePath, string name)
    {
        await MeshService.CreateNode(MeshNode.FromPath(typePath) with
            {
                Name = name,
                NodeType = MeshNode.NodeTypePath,
                State = MeshNodeState.Active,
                Content = new NodeTypeDefinition
                {
                    Configuration = $"config => config.WithContentType<{name}Content>()",
                },
            })
            .SelectMany(_ => MeshService.CreateNode(new MeshNode("api", $"{typePath}/Source")
            {
                NodeType = "Code",
                Name = "api",
                State = MeshNodeState.Active,
                Content = new CodeConfiguration
                {
                    Language = "csharp",
                    Code = $"public record {name}Content {{ public string Title {{ get; init; }} = \"\"; }}",
                },
            }))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

        var good = Definition(await Settled(typePath,
            d => d.CompilationStatus is CompilationStatus.Ok or CompilationStatus.Error, "the first build settles"));
        good.CompilationStatus.Should().Be(CompilationStatus.Ok, $"the source compiles. Error: {good.CompilationError}");
        return good;
    }

    /// <summary>The platform roll, as the seeder's activation finds the record: settled on a build
    /// the live framework cannot run, with nothing in the store under the recorded version.</summary>
    private async Task<MeshNode> TurnFrameworkStale(string typePath)
    {
        await Mesh.ServiceProvider.GetRequiredService<AccessService>()
            .RunAsSystem(() => Mesh.GetMeshNodeStream(typePath).Update(current =>
                current with
                {
                    Content = Definition(current) with
                    {
                        CompiledFrameworkVersion = OtherFramework,
                        LastCompiledVersion = 987_654_321,
                    },
                }))
            .Take(1)
            .Should().Within(TestTimeouts.WriteConvergence)
            .Emit("the record turns framework-stale", cancellationToken: TestContext.Current.CancellationToken);
        return await Settled(typePath,
            d => string.Equals(d.CompiledFrameworkVersion, OtherFramework, StringComparison.Ordinal),
            "the stale record is what the type's hub now serves");
    }

    [Fact(Timeout = 300_000)]
    public async Task AProvisionalAdoption_MadeWhileTheKickoffWasArmed_LeavesTheRecordAlone()
    {
        const string typePath = "type/AdoptedWhileStale";
        await BuiltType(typePath, "AdoptedWhileStale");

        // ── The seeder reserves BEFORE it touches the type, and the activation finds it stale. ──
        var reservation = Adoptions.Reserve(typePath);
        try
        {
            var stale = await TurnFrameworkStale(typePath);

            // ── The adoption finishes PROVISIONAL: recorded over the version it read, stamp held,
            // so the shared record still names the previous platform's build. ──
            Adoptions.RecordAdopted(typePath, stale.Version,
                Definition(stale) with { CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion });
        }
        finally
        {
            reservation.Dispose();
        }

        // ── THE ASSERTION. ─────────────────────────────────────────────────────────────────────
        await KickedOrRebuilt(typePath).Should().NotEmit(Silence,
            "🚨 this process has adopted the type — in flight when the kickoff armed, recorded when it "
            + "cleared. Before the fix the framework-stale kickoff ignored both, flipped Pending under the "
            + "running adoption and (in an untracked partition) parked the type, which the sweep then "
            + "judged a CompileError and the bake gate a regression",
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The negative control: an adoption that was DECLINED (nothing recorded) delays the kickoff and
    /// never cancels it — the stale build is rebuilt once the reservation clears, exactly as it was
    /// before the interlock. Waiting for the reservation is also what fails without the fix: the
    /// unguarded kickoff flips while the adoption is still running.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ADeclinedAdoption_DelaysTheKickoff_AndTheStaleBuildIsStillRebuilt()
    {
        const string typePath = "type/DeclinedWhileStale";
        await BuiltType(typePath, "DeclinedWhileStale");

        var reservation = Adoptions.Reserve(typePath);
        try
        {
            await TurnFrameworkStale(typePath);
            await KickedOrRebuilt(typePath).Should().NotEmit(Silence,
                "🚨 an adoption is in flight for this type — the kickoff waits for it instead of racing it",
                TestContext.Current.CancellationToken);
        }
        finally
        {
            reservation.Dispose();
        }

        var rebuilt = Definition(await Settled(typePath,
            d => d.CompilationStatus == CompilationStatus.Ok
                 && string.Equals(d.CompiledFrameworkVersion, NodeTypeCompilationHelpers.FrameworkVersion,
                     StringComparison.Ordinal),
            "a declined adoption leaves the stale build to the kickoff, which rebuilds it for the live framework"));
        rebuilt.LatestAssemblyPath.Should().NotBeNullOrEmpty();
    }
}
