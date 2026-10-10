using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Updates.Test;

/// <summary>
/// 🚨 <b>A record that names a build the store does not hold is REBUILT by the sweep — it is not
/// counted as baked</b> (Systemorph/MeshWeaver.Plugins#2799).
///
/// <para><b>The incident.</b> On the <c>memex</c> deployment, boot after boot logged
/// <c>DynamicContentTypeRegistration: 15 baked dynamic NodeType(s) could NOT be registered on this
/// replica</c>, each <c>StaleBytes</c>: the record's <c>LatestAssemblyMvid</c> differed from the MVID
/// of the only file the store held under the record's version. The same fifteen types with the
/// same pair of MVIDs each, four generations apart — a standing state, not a roll race. It is the residue of the first-write-wins store: a recompile at an unchanged node version
/// kept build N's file while the record was stamped with N+1's MVID (the shape
/// <see cref="BundleUpdateTest"/> reproduces for a bundle). The store has been content-addressed
/// since, so no new record gets into that state — but nothing ever healed the old ones, because the
/// bake probe asked the store by <c>(path, version)</c> alone, found a file, and reported the type
/// <see cref="BakeState.Baked"/>. The one pass that rebuilds takes its work list from that probe.</para>
///
/// <para><b>Should fail if</b> the probe goes back to asking by version key: the stale record then
/// reads <see cref="BakeState.Baked"/>, the sweep rebuilds nothing, and the registration pass keeps
/// answering <see cref="ContentTypeRegistrationStatus.StaleBytes"/> — the three assertions marked
/// below. The control half (a coherent record is Baked and is left alone) is the first assertion.</para>
/// </summary>
public class RecordNamesABuildTheStoreLacksTest(ITestOutputHelper output) : UpdateScenarioBase(output)
{
    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private async Task<NodeTypeBakeEntry> Probe(string typePath, NodeTypeDefinition definition)
    {
        var report = await NodeTypeBakeStatus
            .Probe(new Dictionary<string, NodeTypeDefinition?> { [typePath] = definition }, Store)
            .Take(1)
            .Should().Within(Step).Emit("the bake probe always answers",
                cancellationToken: TestContext.Current.CancellationToken);
        return report!.Entries.Single();
    }

    /// <summary>The type as the mesh-wide listing shows it — the enumeration both the sweep and the
    /// registration pass decide from — waited on until it shows <paramref name="state"/>.</summary>
    private Task<DynamicTypePreWarmer.DynamicTypes> Listed(
        string typePath, Func<NodeTypeDefinition, bool> state, string because)
        => Observable.Interval(TimeSpan.FromMilliseconds(200)).StartWith(0L)
            .SelectMany(_ => MeshService
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{typePath}").AsSystem())
                .Take(1))
            .Select(change => DynamicTypePreWarmer.DynamicTypesOf(change.Items, Mesh.JsonSerializerOptions, null))
            .Where(types => types.Definitions.TryGetValue(typePath, out var d) && d is not null && state(d))
            .Take(1)
            .Should().Within(Step).Emit(because, cancellationToken: TestContext.Current.CancellationToken);

    private async Task<ContentTypeRegistrationOutcome> Register(
        string typePath, DynamicTypePreWarmer.DynamicTypes types)
    {
        var outcomes = await DynamicContentTypeRegistrar
            .RegisterTypes(Mesh, types, TimeSpan.Zero, null)
            .ToArray()
            .Should().Within(Step + Step).Emit("the registration pass reaches one outcome per type",
                cancellationToken: TestContext.Current.CancellationToken);
        var outcome = outcomes!.Single(o => o.TypePath == typePath);
        Output.WriteLine("{0}: {1} — {2}", outcome.TypePath, outcome.Status, outcome.Detail ?? "(no detail)");
        return outcome;
    }

    [Fact(Timeout = 300_000)]
    public async Task ARecordNamingABuildTheStoreLacks_IsRebuiltByTheSweep_AndThenRegisters()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var (typePath, n) = await CreateType("N");
        n.LatestAssemblyMvid.Should().NotBeNullOrEmpty("build N stamps the identity of its bytes");

        (await Probe(typePath, n)).State.Should().Be(BakeState.Baked,
            "CONTROL — a record whose MVID is the store's build is baked; if this is ever "
            + "BytesMissing the probe rebuilds every healthy type on every boot");

        // The first-write-wins residue: the record names N+1's identity, the store kept N's bytes
        // under the same version, and no file anywhere carries the MVID the record names.
        var neverStored = Guid.NewGuid().ToString("N");
        var stale = await StampBuild(typePath,
            d => d with { LatestAssemblyMvid = neverStored },
            d => d.LatestAssemblyMvid == neverStored);
        var listedStale = await Listed(typePath, d => d.LatestAssemblyMvid == neverStored,
            "the listing shows the record that names a build the store lacks");

        (await Register(typePath, listedStale!)).Status.Should().Be(ContentTypeRegistrationStatus.StaleBytes,
            "THE SYMPTOM — the registration pass refuses bytes that are not the build the record "
            + "names, which is what the memex deployment logged for fifteen types, boot after boot");

        var entry = await Probe(typePath, stale);
        Output.WriteLine("probe: {0} — {1}", entry.State, entry.Detail ?? "(no detail)");
        entry.State.Should().Be(BakeState.BytesMissing,
            "SHOULD-FAIL-IF the probe asks by version key — the store holds A file under the "
            + "record's version, but not the build the record names, and only that is 'baked'");
        entry.Detail.Should().Contain(neverStored, "the entry names the build the record claims");

        // A later platform build, so "this image produced it" cannot be what answers below.
        const string laterBuild = "9999.0.0-ci.1";
        entry.IsRegressionBaselineFor(laterBuild).Should().BeFalse(
            "the build this record names was never available to any replica, so a failed rebuild "
            + "takes nothing away — it must not refuse a new replica's readiness and stall the roll");
        new NodeTypeBakeEntry(typePath, BakeState.BytesMissing) { ProducedByPlatformBuild = entry.ProducedByPlatformBuild }
            .IsRegressionBaselineFor(laterBuild).Should().BeTrue(
                "CONTROL — an ordinary store miss of a working build IS a regression baseline");

        // The sweep takes its work list from that probe: a store miss is rebuilt on the owner.
        var swept = await DynamicTypePreWarmer
            .WarmDynamicTypes(Mesh, perTypeBudget: Step + Step, betweenTypes: TimeSpan.Zero)
            .ToList()
            .Should().Within(TimeSpan.FromMinutes(4)).Emit("the sweep reaches one outcome per type",
                cancellationToken: TestContext.Current.CancellationToken);
        var outcome = swept!.Single(o => o.TypePath == typePath);
        Output.WriteLine("sweep: {0} — {1}", outcome.Status, outcome.Detail ?? "(no detail)");
        outcome.Status.Should().Be(PreWarmStatus.Compiled,
            "SHOULD-FAIL-IF the sweep still reads the type as already baked — nothing rebuilds it then");

        var healed = await DefinitionWhere(typePath,
            d => d.CompilationStatus == CompilationStatus.Ok
                 && !string.IsNullOrEmpty(d.LatestAssemblyMvid)
                 && d.LatestAssemblyMvid != neverStored,
            "the rebuild re-stamps the identity of the bytes it stored");
        var resolved = await Store
            .TryGetBuildPath(typePath, healed.LastCompiledVersion!.Value, healed.LatestAssemblyPath, healed.LatestAssemblyMvid)
            .Take(1)
            .Should().Within(Step).Emit(cancellationToken: TestContext.Current.CancellationToken);
        ServedBuildIdentity.OfFile(resolved).Should().Be(healed.LatestAssemblyMvid,
            "version, path and MVID are ONE reference again — the store holds the build the record names");
        (await Probe(typePath, healed)).State.Should().Be(BakeState.Baked,
            "and the next boot's probe leaves it alone: the rebuild converges, it does not repeat");

        var listedHealed = await Listed(typePath, d => d.LatestAssemblyMvid == healed.LatestAssemblyMvid,
            "the listing shows the re-stamped record");
        (await Register(typePath, listedHealed!)).Status.Should().NotBe(ContentTypeRegistrationStatus.StaleBytes,
            "SHOULD-FAIL-IF the record still names a build the store lacks — the pass can register "
            + "from the store's bytes once they ARE the published build");
    }
}
