using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Compiler;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A process its own readiness validation REFUSES must not join the mesh — issue #3478.</b>
///
/// <para><b>The incident.</b> <c>memex.systemorph.com</c>, 2026-09-06. Two replicas served
/// <c>3.0.0-rc9.ci.7693</c>. A roll to <c>ci.7926</c> started at 19:17Z; the NodeType bake readiness
/// gate measured a regression (<c>Feedback/Feedback</c>) and refused readiness, and Kubernetes
/// correctly stalled the rollout with the previous image still serving. <b>The refused pod kept
/// running inside the mesh for two hours.</b> It stamped <c>Crm/Offer</c> at 19:28:04 and
/// <c>Crm/Opportunity</c> at 19:31:30 with its own framework identity (<c>sc273ee39f…</c>) onto the
/// shared NodeType records the serving replicas (<c>s2f227642d…</c>) read — and every deal and
/// offer page on the client portal was dead until 21:50Z (#3472). Readiness gates TRAFFIC; it never
/// gated MEMBERSHIP.</para>
///
/// <para><b>What these cases discriminate, and why a weaker test would prove nothing.</b> The pod
/// in the incident was ALREADY not-ready and the outage happened anyway, so asserting "B is not
/// ready" is vacuous. Every case here asserts on the DURABLE NodeType records, read back through
/// the mesh's own <see cref="IStorageAdapter"/> — the same rows the serving replicas read:</para>
/// <list type="bullet">
/// <item><see cref="ARegressedBake_StampsNothing_TheRecordsStillCarryImageA"/> — the fix. Image B's
/// sweep regresses ⇒ ZERO of its stamps land, on records it had already compiled BEFORE the
/// regression was discovered.</item>
/// <item><see cref="ACleanBake_StampsEverythingItHeld"/> — the CONTROL. The identical sweep, passing
/// ⇒ every stamp lands. Without it the first case would pass against an implementation that simply
/// never stamps, and the test could not see a stamp at all.</item>
/// <item><see cref="AnUnarmedGate_StillStamps_ReproducingTheTwoImageWindow"/> — the two-image
/// window, reproduced. With no admission authority armed the regressing sweep stamps exactly as it
/// did on 2026-09-06, so the protection is measurably the thing that changed and not the
/// harness.</item>
/// <item><see cref="ARegressionFoundAfterAdmission_StopsFurtherStamping"/> — the other direction:
/// a process that becomes unhealthy AFTER joining stops publishing, because the verdict is
/// level-triggered rather than latched at admission.</item>
/// </list>
///
/// <para>The mesh is REAL (<see cref="MonolithMeshTestBase"/>) and the write under test is the
/// production one — <c>NodeTypeBatchBake.WriteStamp</c>, the storage-level compare-and-set the batch
/// bake uses for every type it compiles. Only the bake VERDICT is driven directly, exactly as the
/// sweep drives it (<c>MarkRunning</c> → <c>MarkOutcome</c> → <c>MarkComplete</c>).</para>
/// </summary>
public class RefusedProcessDoesNotJoinTheMeshTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The framework identity the two SERVING replicas compiled against, per #3472.</summary>
    private const string ImageAFramework = "s2f227642d43f78eab13720c4af06a1be";

    /// <summary>The module set those replicas ran — the "module generation" the issue names.</summary>
    private const string ImageAModules = "8f251f57458afe55c000a13802e347b3";

    /// <summary>Where image A's bytes live. Image B stamps a different key.</summary>
    private const string AssemblyCollection = "nodetype-cache";

    /// <summary>The revision the serving replicas' stamp left each record at.</summary>
    private const long SeededVersion = 11;

    /// <summary>The NodeTypes the incident killed, under this test's partition.</summary>
    private static readonly IReadOnlyList<string> Types = ["Offer", "Opportunity", "Feedback"];

    /// <summary>
    /// The bake gate this process (image B) is running with. Reassigned per case — armed, unarmed —
    /// and read through <see cref="LiveBakeGate"/> so the mesh built in the constructor always sees
    /// the CURRENT one. The real <see cref="NodeTypeBakeGateState"/> throughout: the point is to
    /// exercise its own <c>Admission</c> derivation, never a stand-in for it.
    /// </summary>
    private NodeTypeBakeGateState bake = new() { GatesReadiness = true };

    /// <summary>
    /// Registers this process's bake gate as the mesh's admission authority — the ONE wiring the
    /// portal does (<c>AddNodeTypeBakeGate</c>) and the whole of what arms the publication gate.
    /// </summary>
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .ConfigureServices(services =>
                services.AddSingleton<IMeshAdmissionAuthority>(new LiveBakeGate(() => bake)));

    private IStorageAdapter Storage => Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();

    private MeshPublicationGate Publication =>
        Mesh.ServiceProvider.GetRequiredService<MeshPublicationGate>();

    /// <summary>The module fingerprint THIS process would stamp — image B's module generation.</summary>
    private string ImageBModules =>
        Mesh.ServiceProvider.GetRequiredService<InstalledModulesFingerprint>().Hash;

    // ── the scenario ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 THE FIX. Image B bakes; its sweep regresses on the third type; NOTHING it compiled reaches
    /// the mesh — including the two types it had already compiled successfully before the regression
    /// was discovered, which is precisely the pair the incident lost.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ARegressedBake_StampsNothing_TheRecordsStillCarryImageA()
    {
        var seeded = await SeedRecordsStampedByImageA();

        // Image B's sweep: two clean compiles, then the regression that refuses the image.
        bake.MarkRunning("enumerating dynamic NodeTypes");
        await StampAsImageB(seeded["Offer"]);
        await StampAsImageB(seeded["Opportunity"]);
        bake.MarkOutcome(Regression("Feedback"));
        Publication.Reconsider();
        bake.MarkComplete("baked in 00:02:11 — compiled=2 alreadyBaked=0");
        Publication.Reconsider();

        bake.Phase.Should().Be(BakePhase.Regressed, "the sweep measured a real regression");
        bake.Admission.Should().Be(MeshAdmission.Refused,
            "a bake that refuses readiness must refuse MEMBERSHIP by the same verdict — that "
            + "equivalence is the whole of #3478's directive");

        foreach (var type in new[] { "Offer", "Opportunity" })
            (await ReadStamp(type)).Should().Be(
                ImageAStamp(type),
                $"a refused process must leave {type}'s record exactly as the SERVING replicas "
                + "stamped it — a record naming image B's assembly is what made the type "
                + "unloadable on 2026-09-06");

        Publication.WithheldCount.Should().Be(2,
            "both stamps must be accounted for as withheld — a silently-vanished publication is "
            + "indistinguishable from one that was never offered");
        Publication.PassedCount.Should().Be(0, "nothing may pass while the verdict is not admitted");
        Publication.ReleasedCount.Should().Be(0, "a refused verdict releases nothing");
    }

    /// <summary>
    /// 🚨 THE CONTROL — and the arm that makes the case above non-vacuous. The identical sweep, with
    /// no regression, stamps EVERYTHING it held. Held publications are written after the verdict,
    /// not before it, which is "validate before running" and not "never run".
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ACleanBake_StampsEverythingItHeld()
    {
        var seeded = await SeedRecordsStampedByImageA();

        bake.MarkRunning("enumerating dynamic NodeTypes");
        foreach (var type in Types)
            await StampAsImageB(seeded[type]);

        // Mid-sweep the process is provisional: it has compiled, and published NOTHING.
        Publication.HeldCount.Should().Be(Types.Count,
            "every stamp waits for the verdict — a stamp written before the sweep ends is the "
            + "defect, whichever way the sweep then goes");
        foreach (var type in Types)
            (await ReadStamp(type)).Should().Be(
                ImageAStamp(type), "nothing is published while the verdict is still forming");

        bake.MarkComplete("baked in 00:02:40 — compiled=3 alreadyBaked=0");
        Publication.Reconsider();

        bake.Admission.Should().Be(MeshAdmission.Admitted, "a clean sweep admits this process");
        Publication.ReleasedCount.Should().Be(Types.Count, "every held stamp is written on admission");
        Publication.WithheldCount.Should().Be(0, "a clean sweep withholds nothing");

        foreach (var type in Types)
            (await ReadStamp(type)).Should().Be(
                ImageBStamp(type),
                $"{type}'s record must now name image B's build — holding a stamp forever would "
                + "trade an outage for a platform that never records what it compiled");
    }

    /// <summary>
    /// 🚨 THE TWO-IMAGE WINDOW, REPRODUCED. With no admission authority armed — the state every
    /// deployment that has not switched the bake readiness gate on is in, and the state the
    /// protection is measured AGAINST — the very same regressing sweep stamps every record with
    /// image B's identity, exactly as it did on 2026-09-06.
    ///
    /// <para>This is deliberately not a "revert the fix" experiment kept outside the suite: it is
    /// the pre-fix behaviour, permanently observable, so a future change that quietly disarms the
    /// gate cannot look like a pass. It also pins the fail-OPEN default — an unarmed deployment is
    /// never black-holed by enforcement nobody opted into.</para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AnUnarmedGate_StillStamps_ReproducingTheTwoImageWindow()
    {
        bake = new NodeTypeBakeGateState { GatesReadiness = false };
        var seeded = await SeedRecordsStampedByImageA();

        bake.MarkRunning("enumerating dynamic NodeTypes");
        await StampAsImageB(seeded["Offer"]);
        await StampAsImageB(seeded["Opportunity"]);
        bake.MarkOutcome(Regression("Feedback"));
        Publication.Reconsider();
        bake.MarkComplete("baked in 00:02:11 — compiled=2 alreadyBaked=0");
        Publication.Reconsider();

        bake.Phase.Should().Be(BakePhase.Regressed, "the sweep measured the same regression");
        bake.Admission.Should().Be(MeshAdmission.Unarmed,
            "nothing consumes this gate's verdict, so it enforces nothing — 'registered' and "
            + "'armed' stay separate");

        foreach (var type in new[] { "Offer", "Opportunity" })
            (await ReadStamp(type)).Should().Be(
                ImageBStamp(type),
                "THIS is the two-image window: one mesh, two images, and the record now points at "
                + "an assembly the serving replicas cannot load");
        Publication.PassedCount.Should().Be(2, "an unarmed gate is a straight pass-through");
        Publication.WithheldCount.Should().Be(0, "an unarmed gate withholds nothing");
    }

    /// <summary>
    /// 🚨 THE OTHER DIRECTION — a process that becomes unhealthy AFTER joining. The sweep completes
    /// clean, the process is admitted and stamps; a later compile then regresses a
    /// previously-healthy type, and from that moment the process publishes nothing more. The verdict
    /// is re-read at every publication rather than latched at admission, so this needs no second
    /// mechanism.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ARegressionFoundAfterAdmission_StopsFurtherStamping()
    {
        var seeded = await SeedRecordsStampedByImageA();

        bake.MarkRunning("enumerating dynamic NodeTypes");
        bake.MarkComplete("baked in 00:00:04 — compiled=0 alreadyBaked=3");
        Publication.Reconsider();
        bake.Admission.Should().Be(MeshAdmission.Admitted, "the sweep was clean");

        await StampAsImageB(seeded["Offer"]);
        (await ReadStamp("Offer")).Should().Be(
            ImageBStamp("Offer"), "an admitted process publishes normally");

        // A lazy compile, hours later, breaks a type that was healthy on this image.
        bake.MarkOutcome(Regression("Feedback"));
        bake.Admission.Should().Be(MeshAdmission.Refused,
            "the verdict is level-triggered — admission is not a latch");

        await StampAsImageB(seeded["Opportunity"]);
        (await ReadStamp("Opportunity")).Should().Be(
            ImageAStamp("Opportunity"),
            "once this process is refused it publishes nothing more, whatever it published before");
        Publication.RefusedCount.Should().Be(1, "the refusal is counted, not silent");
    }

    /// <summary>
    /// 🚨 SELF-HEALING SURVIVES THE GATE — the arm that proves this change did not trade one outage
    /// class for another. #1214: a bake that compiles a half-applied content update records FALSE
    /// regressions, the platform recompiles the type by itself once the content converges, and the
    /// gate retracts so the pod goes Ready without a human. That retraction watched the type's
    /// SHARED RECORD — which a refused process can no longer move, because its stamps are now
    /// withheld. A record-only watch would therefore wait forever, and #1214's self-healing stall
    /// would become a permanent one.
    ///
    /// <para>The retraction now also observes the PROCESS-LOCAL fact
    /// (<see cref="LocalNodeTypeBuilds"/>) that the record was ever a proxy for. Here the record is
    /// deliberately never touched, so the only witness that can retract is the local one.</para>
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task ALocallyRebuiltTypeRetractsItsRegression_EvenThoughTheRecordCannotMove()
    {
        var seeded = await SeedRecordsStampedByImageA();
        var condemned = $"{TestPartition}/Feedback";

        bake.MarkRunning("enumerating dynamic NodeTypes");
        await StampAsImageB(seeded["Offer"]);
        bake.MarkOutcome(Regression("Feedback"));
        Publication.Reconsider();
        bake.Admission.Should().Be(MeshAdmission.Refused, "the sweep condemned a healthy type");

        using var watch = DynamicTypePreWarmer.WatchForRecovery(
            Mesh, bake, condemned,
            Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("recovery"));

        // The content converged and the platform rebuilt the type HERE. Re-raised on the sanctioned
        // poll rather than once, because the watch subscribes its second witness only after the
        // node stream has produced a baseline — a single hot emission could precede that.
        var builds = Mesh.ServiceProvider.GetRequiredService<LocalNodeTypeBuilds>();
        await Observable.Interval(TimeSpan.FromMilliseconds(50)).StartWith(0L)
            .Do(_ => builds.RecordUsableBuild(condemned))
            .Where(_ => bake.Regressions.Count == 0)
            .FirstAsync()
            .Timeout(TimeSpan.FromSeconds(20))
            .Await();

        bake.Retracted.Keys.Should().Contain(condemned,
            "the retraction must SAY the regression was withdrawn — a silently-vanished one is "
            + "indistinguishable from one that never happened");
        bake.Admission.Should().NotBe(MeshAdmission.Refused,
            "a process with no standing regression is no longer refused — readiness and membership "
            + "move together in BOTH directions");
    }

    // ── harness ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A DIRECTLY-MEASURED regression on a type that was healthy before this image — the only
    /// outcome shape that gates (a timeout is not a verdict; an already-broken type does not freeze
    /// the platform).
    /// </summary>
    private static PreWarmOutcome Regression(string type) =>
        new($"{TestPartition}/{type}", PreWarmStatus.CompileError,
            "CS0117: 'Localizer' does not contain a definition for 'Title'")
        {
            WasHealthyBeforeBake = true,
        };

    /// <summary>The full stamp, as the two serving replicas left it.</summary>
    private (string? Framework, string? Modules, string? Collection, string? Assembly, long? Version)
        ImageAStamp(string type)
        => (ImageAFramework, ImageAModules, AssemblyCollection, $"{type}/v11-s2f22764-A.dll", 11L);

    /// <summary>The full stamp image B writes — a different framework, module set and assembly key.</summary>
    private (string? Framework, string? Modules, string? Collection, string? Assembly, long? Version)
        ImageBStamp(string type)
        => (FrameworkBuildIdentity.FrameworkVersion, ImageBModules, AssemblyCollection,
            $"{type}/v22488-sc273ee3-B.dll", 22488L);

    /// <summary>
    /// Writes each NodeType record as the SERVING replicas stamped it, storage-level (no change
    /// feed, so no hub is woken and nothing recompiles behind the test). Returns the persisted
    /// nodes, whose versions the compare-and-set stamp then races.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, MeshNode>> SeedRecordsStampedByImageA()
    {
        var seeded = new Dictionary<string, MeshNode>();
        foreach (var type in Types)
        {
            var stored = await Storage.Write(
                new MeshNode(type, TestPartition)
                {
                    NodeType = MeshNode.NodeTypePath,
                    // A real revision counter. Version 0 means "never versioned", and the
                    // compare-and-set reads an expected version of 0 as "insert only if absent" —
                    // so a row seeded at 0 would make every stamp lose for a reason that has
                    // nothing to do with admission.
                    Version = SeededVersion,
                    Content = new NodeTypeDefinition
                    {
                        CompilationStatus = CompilationStatus.Ok,
                        CompiledFrameworkVersion = ImageAFramework,
                        CompiledModulesHash = ImageAModules,
                        LatestAssemblyCollection = AssemblyCollection,
                        LatestAssemblyPath = $"{type}/v11-s2f22764-A.dll",
                        LastCompiledVersion = 11,
                        LastCompileSucceededAt = new DateTimeOffset(
                            2026, 9, 6, 19, 19, 38, TimeSpan.Zero),
                    },
                },
                Mesh.JsonSerializerOptions).Await();
            seeded[type] = stored!;
        }
        return seeded;
    }

    /// <summary>
    /// Drives the PRODUCTION stamp for one type as image B — the same storage-level compare-and-set
    /// the batch bake performs after every compile.
    /// </summary>
    private Task StampAsImageB(MeshNode typeNode) =>
        NodeTypeBatchBake.WriteStamp(
                Mesh,
                typeNode,
                ok: true,
                new NodeCompilationResult(
                    // A path that does not exist: ServedBuildIdentity.OfFile then leaves the MVID
                    // stamp alone, exactly as it does for a producer with no readable file. The
                    // assembly COORDINATES below are what discriminate the two images.
                    AssemblyLocation: $"/nonexistent/{typeNode.Id}-B.dll",
                    NodeTypeConfigurations: [],
                    Collection: AssemblyCollection,
                    ContentPath: $"{typeNode.Id}/v22488-sc273ee3-B.dll",
                    Version: 22488),
                error: null,
                releasePath: null,
                startedAt: DateTimeOffset.UtcNow,
                // A real logger: the stamp is best-effort by design, so a compare-and-set that is
                // REFUSED (or content that cannot be read back as a NodeTypeDefinition) is reported
                // only in the log. Without it a case that fails here says "the record did not
                // change" and nothing about why.
                Mesh.ServiceProvider.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("RefusedProcessDoesNotJoinTheMesh"))
            .Await();

    /// <summary>Reads one NodeType's stamp back off the DURABLE row — what a peer replica reads.</summary>
    private async Task<(string? Framework, string? Modules, string? Collection, string? Assembly, long? Version)>
        ReadStamp(string type)
    {
        var node = await Storage.Read($"{TestPartition}/{type}", Mesh.JsonSerializerOptions)
            .Take(1).Await();
        var def = node?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions);
        return (def?.CompiledFrameworkVersion, def?.CompiledModulesHash,
            def?.LatestAssemblyCollection, def?.LatestAssemblyPath, def?.LastCompiledVersion);
    }

    /// <summary>
    /// Reads the CURRENT bake gate on every call, so a case can install its own (armed / unarmed)
    /// after the mesh — built in the base constructor — has already resolved its authorities. It
    /// adds no verdict of its own: <see cref="NodeTypeBakeGateState.Admission"/> is the predicate
    /// under test.
    /// </summary>
    private sealed class LiveBakeGate(Func<NodeTypeBakeGateState> current) : IMeshAdmissionAuthority
    {
        public MeshAdmission Admission => current().Admission;

        public string AdmissionReason => current().AdmissionReason;
    }
}
