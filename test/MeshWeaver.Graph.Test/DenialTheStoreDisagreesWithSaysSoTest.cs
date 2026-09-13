using System.Reactive.Linq;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>#4061, finding 2 — a denial the durable STORE disagrees with must say so.</b>
///
/// <para>The intermittent failure this issue reports (<c>SpaceDeletionPartitionDropTests</c>, a
/// Space deleted and recreated under the same id) surfaces as
/// <c>Access denied: Create permission required for node '&lt;space&gt;/page'</c> and nothing
/// else — while the same run's log shows the creator's grant written durably and RETURNED before
/// the denied create began. The permission fold reads grants through a process-wide cached, synced
/// QUERY (<c>PermissionEvaluator.ObserveEffectiveAssignments</c> →
/// <c>SecurityQuery</c> → <c>IMeshNodeStreamCache.GetQuery</c>), and a decision taken from a
/// snapshot older than the write that authorised it denies a right the store already grants.</para>
///
/// <para><b>What this test does NOT claim.</b> It does not reproduce the race — see the issue for
/// the two controls that stay green on an in-memory mesh. It pins the property that makes the race
/// LEGIBLE the next time it happens: the one denial that is provably suspect used to produce the
/// LEAST information, because <c>DescribeOwnerlessPartition</c> answers <c>null</c> as soon as
/// grants exist, which is exactly the case here.</para>
///
/// <para>The state is constructed rather than raced: the grant is written straight through
/// <see cref="IStorageAdapter"/>, so the store holds it and the cached security query never learns
/// of it — which is the state the race produces, reached deterministically.</para>
/// </summary>
public class DenialTheStoreDisagreesWithSaysSoTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string SpaceId = "disagreeprobe";
    private const string Ghost = "ghost-grantee";
    private const string Stranger = "stranger-nograntee";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => Bootstrap.Bootstrap(builder)
            .AddRowLevelSecurity()
            .AddGraph()
            .AddSpaceType()
            .ConfigureHub(c => c
                .WithQuiesceTimeout(TestQuiesceTimeout)
                .WithRequestTimeout(TimeSpan.FromSeconds(60)));

    private async Task GivenTheSpaceExists()
    {
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNode(new MeshNode(SpaceId)
        {
            Name = "Disagreement Probe", NodeType = SpaceNodeType.NodeType,
            State = MeshNodeState.Active, Content = new Space(),
        }).Should().Within(TestTimeouts.CrossSilo).Emit("the creator may create a Space");
    }

    /// <summary>
    /// Puts a node AT THE GRANT PATH that the permission fold does not honour, while the durable
    /// store lists it — the state the CI race produces, reached deterministically.
    ///
    /// <para>🚨 Constructed by CONTENT, not by timing. A grant written straight through
    /// <see cref="IStorageAdapter"/> was the first attempt and it does NOT produce the state: the
    /// adapter's own change feed carries it, the cached security query sees it, and the principal
    /// is ALLOWED (measured — the test failed with "No exception was thrown"). Which is itself
    /// worth knowing, and is recorded on the issue.</para>
    ///
    /// <para>So the node carries a NodeType the fold skips. <c>ComputeScopeRoles</c> reads only
    /// nodes whose <c>NodeType</c> is <c>AccessAssignment</c>, while the storage probe lists paths
    /// — which is exactly the shape a grant whose content degraded to an untyped
    /// <c>JsonElement</c> presents, one of the two causes the message names.</para>
    /// </summary>
    private async Task GivenTheStoreHoldsANodeAtTheGrantPathTheFoldDoesNotHonour(string principal)
    {
        var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
        await storage.Write(
            new MeshNode($"{principal}_Access", $"{SpaceId}/_Access")
            {
                NodeType = "Markdown",
                Name = $"{principal} Access",
                MainNode = SpaceId,
            },
            Mesh.JsonSerializerOptions)
            .Should().Within(TestTimeouts.CrossSilo).Emit("the direct store write must land");
    }

    private async Task<string> DenialMessageFor(string principal, string childId)
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        using (access.SwitchAccessContext(new AccessContext { ObjectId = principal }))
        {
            var thrown = await Assert.ThrowsAnyAsync<Exception>(async () =>
                await meshService.CreateNode(new MeshNode(childId, SpaceId)
                {
                    Name = childId, NodeType = "Markdown", State = MeshNodeState.Active,
                }).Timeout(TestTimeouts.CrossSilo).FirstAsync());
            Output.WriteLine($"{principal} → {thrown.Message}");
            return thrown.Message;
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task ADeniedWriteWhoseGrantTheStoreHolds_NamesTheDisagreement()
    {
        await GivenTheSpaceExists();
        await GivenTheStoreHoldsANodeAtTheGrantPathTheFoldDoesNotHonour(Ghost);

        var message = await DenialMessageFor(Ghost, "ghost-page");

        Assert.Contains("Create permission required", message, StringComparison.Ordinal);
        // 🚨 The property. Before this the message stopped at the line above — and the case in
        // which the denial is LEAST trustworthy produced the LEAST information, because the
        // ownerless-partition probe answers null as soon as any grant exists.
        Assert.Contains("holds a node at", message, StringComparison.Ordinal);
        Assert.Contains(Ghost, message, StringComparison.Ordinal);
        Assert.Contains($"{SpaceId}/_Access/{Ghost}_Access", message, StringComparison.Ordinal);
        Assert.Contains("MeshWeaver#4061", message, StringComparison.Ordinal);
    }

    [Fact(Timeout = 180_000)]
    public async Task ANORDINARYDenialSaysNothingOfTheKind()
    {
        await GivenTheSpaceExists();
        // The partition HAS grants (the creator's), so the ownerless diagnosis stays silent here
        // exactly as it does above — the only difference between the two cases is whose grant.
        await GivenTheStoreHoldsANodeAtTheGrantPathTheFoldDoesNotHonour(Ghost);

        var message = await DenialMessageFor(Stranger, "stranger-page");

        Assert.Contains("Create permission required", message, StringComparison.Ordinal);
        // 🚨 THE NEGATIVE CONTROL. A principal the store holds NO grant for is an ordinary
        // permission decision and must read as one. If the note were a constant of every denial on
        // a partition that has grants — the cheap way to write this — it would appear here too,
        // and the assertion above would be measuring nothing.
        Assert.DoesNotContain("holds a node at", message, StringComparison.Ordinal);
        Assert.DoesNotContain("MeshWeaver#4061", message, StringComparison.Ordinal);
    }
}
