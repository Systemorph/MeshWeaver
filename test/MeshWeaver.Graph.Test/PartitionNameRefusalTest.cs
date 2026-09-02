using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Graph.Security;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A partition name IS a backing-store schema name, and there is ONE rule for it
/// (Systemorph/MeshWeaver#2900 §3, #714).</b>
///
/// <para>The ACA production database carried three schemas named after things that were never
/// partitions — a login redirect with its error query, a search URL with its query string, and a
/// UPN. Each was fully provisioned (a <c>mesh_nodes</c> table and all), each costs a cross-schema
/// fan-out entry forever, and each is one careless <c>DROP SCHEMA</c> away from confusion with real
/// data. They were created by a pre-#714 image (the ACA deployment still runs 2026-06-03 at
/// db_version 32; the rule landed 2026-06-05 and its cleanup is V51). On today's code the rule is
/// enforced at every seam that can turn a string into a schema — the Postgres provider's
/// <c>EnsurePartitionProvisioned</c> and path router (MeshWeaver.Plugins), the partition
/// bootstrap in <c>MeshExtensions</c>, and <see cref="OwnsPartitionProvisioningValidator"/> on a
/// <c>Space</c> create — but NOTHING in this repo pinned it: the guard had no test naming the
/// shapes it exists to refuse, so a relaxation would have passed CI silently. This fixture uses
/// the incident's exact three names, plus the shapes the rule must keep admitting, so a change to
/// the rule fails here with the offending name in the message.</para>
/// </summary>
public class PartitionNameRefusalTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The three schemas the ACA discovery listing showed, verbatim (#2900).</summary>
    private static readonly string[] Incident =
    [
        "login?error=auth_failed",
        "search?q=privacy&hq=scope%3adescendants",
        "rbuergi@systemorph.com",
    ];

    public static IEnumerable<object[]> IncidentNames => Incident.Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(IncidentNames))]
    public void TheRule_RefusesEveryNameTheIncidentProduced(string name)
        => PartitionDefinition.IsValidPartitionSegment(name).Should().BeFalse(
            $"'{name}' became a Postgres schema on a pre-#714 image; the rule exists to refuse exactly this shape");

    [Theory]
    [InlineData("acme")]
    [InlineData("acme-2.0_beta")]
    [InlineData("müller")]          // letters are Unicode, not ASCII — an accented name is legitimate
    [InlineData("7days")]           // may start with a digit
    public void TheRule_AdmitsALegitimatePartitionName(string name)
        => PartitionDefinition.IsValidPartitionSegment(name).Should().BeTrue();

    [Theory]
    [InlineData("_Access")]         // a satellite container / global-satellite namespace, never a partition
    [InlineData("-leading-dash")]
    [InlineData("")]
    public void TheRule_RefusesTheStructuralShapes(string name)
        => PartitionDefinition.IsValidPartitionSegment(name).Should().BeFalse();

    [Fact]
    public void TheRule_CountsBytesNotChars()
    {
        // 63 is Postgres' NAMEDATALEN — a BYTE limit that silently truncates. A 63-char name of
        // 2-byte letters is 126 bytes: the router would compute the untruncated schema name and
        // never route back to what Postgres actually stored.
        PartitionDefinition.IsValidPartitionSegment(new string('a', 63)).Should().BeTrue();
        PartitionDefinition.IsValidPartitionSegment(new string('a', 64)).Should().BeFalse();
        PartitionDefinition.IsValidPartitionSegment(new string('ü', 32)).Should().BeFalse("32 × 2 bytes = 64 > 63");
    }

    /// <summary>
    /// The core creation seam: creating a partition-owning node (<c>Space</c>) is the ONE path in
    /// this repo that provisions a schema, and its validator must refuse BEFORE any provider is
    /// asked — with a message that names the offending id and states the rule.
    /// </summary>
    [Theory]
    [MemberData(nameof(IncidentNames))]
    public async Task TheCreationSeam_RefusesASpaceNamedLikeTheIncident_BeforeProvisioning(string name)
    {
        var validator = new OwnsPartitionProvisioningValidator(
            Mesh, Mesh.ServiceProvider.GetRequiredService<ILogger<OwnsPartitionProvisioningValidator>>());

        var result = await validator
            .Validate(new NodeValidationContext
            {
                Operation = NodeOperation.Create,
                Node = new MeshNode(name) { NodeType = "Space", Name = name },
            })
            .Should().Emit();

        result.IsValid.Should().BeFalse();
        result.Reason.Should().Be(NodeRejectionReason.InvalidPath);
        result.ErrorMessage.Should().Contain(name)
            .And.Contain(PartitionDefinition.PartitionSegmentRequirement);
    }

    /// <summary>
    /// End to end through <see cref="IMeshService.CreateNode"/>: the refusal is what the caller
    /// gets, not a provisioned partition. Positive control first — the same call with a valid name
    /// succeeds, so a refusal below is the rule and not a broken harness.
    /// </summary>
    [Fact]
    public async Task CreateNode_RefusesTheIncidentNames_AndAcceptsAValidOne()
    {
        var accepted = await NodeFactory
            .CreateNode(new MeshNode("partition-name-control") { NodeType = "Space", Name = "control" })
            .Timeout(TestTimeouts.Convergence);
        accepted.Path.Should().Be("partition-name-control");

        foreach (var name in Incident)
        {
            var fault = await Assert.ThrowsAnyAsync<Exception>(async () =>
                await NodeFactory
                    .CreateNode(new MeshNode(name) { NodeType = "Space", Name = name })
                    .Timeout(TestTimeouts.Convergence));
            fault.Message.Should().Contain(PartitionDefinition.PartitionSegmentRequirement,
                $"'{name}' must be refused by the partition-name rule, not by anything downstream of it");
        }
    }
}
