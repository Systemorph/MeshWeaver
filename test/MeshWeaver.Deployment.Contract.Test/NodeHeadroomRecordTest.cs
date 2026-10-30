using System.Collections.Immutable;
using MeshWeaver.Deployment;
using Xunit;

namespace MeshWeaver.Deployment.Contract.Test;

/// <summary>
/// The control record's <c>nodeHeadroom</c> (Memex#863, Doc/Architecture/DeploymentAKS → "A spare slot per
/// node pool"): one spare slot per node pool, rendered by the chart as a negative-priority placeholder. Pinned
/// here: the block round-trips the record's JSON; an absent block reads as NONE (empty, never a default slot);
/// replicas default to one; the block is init-only; and <see cref="NodeHeadroomSlot.ProblemsOf"/> names every
/// shape the chart could not render, while the record's own shape passes.
/// </summary>
public class NodeHeadroomRecordTest
{
    private const string Json = """
        {
          "$type": "DeploymentContent",
          "host": "control.example.com",
          "nodeHeadroom": [
            { "pool": "silos", "cpu": "4", "memory": "8Gi" },
            { "pool": "cibuildv6", "cpu": "3500m", "memory": "12Gi", "replicas": 2 }
          ]
        }
        """;

    [Fact]
    public void TheRecordsJson_CarriesEveryPool_AndWritesItBack()
    {
        var record = DeploymentRecordJson.Read(Json)!;

        Assert.Equal(["silos", "cibuildv6"], record.NodeHeadroom.Select(s => s.Pool));
        Assert.Equal(("4", "8Gi", NodeHeadroomSlot.DefaultReplicas), (record.NodeHeadroom[0].Cpu, record.NodeHeadroom[0].Memory, record.NodeHeadroom[0].Replicas));
        Assert.Equal(2, record.NodeHeadroom[1].Replicas);
        Assert.Empty(NodeHeadroomSlot.ProblemsOf(record.NodeHeadroom));

        var reread = DeploymentRecordJson.Read(DeploymentRecordJson.Write(record))!;
        Assert.Equal(record.NodeHeadroom, reread.NodeHeadroom);
    }

    [Fact]
    public void AnAbsentBlock_IsNoSlot_NeverADefaultOne()
    {
        var record = DeploymentRecordJson.Read("""{ "$type": "DeploymentContent", "host": "x.example.com" }""")!;
        Assert.Empty(record.NodeHeadroom);
        Assert.Empty(NodeHeadroomSlot.ProblemsOf(record.NodeHeadroom));
    }

    [Theory]
    [InlineData("", "4", "8Gi", 1, "pool")]
    [InlineData("Silos", "4", "8Gi", 1, "pool")]               // upper case is no AKS pool name
    [InlineData("silospoolnamelong", "4", "8Gi", 1, "pool")]    // longer than 12
    [InlineData("silos", "", "8Gi", 1, "cpu")]
    [InlineData("silos", "four", "8Gi", 1, "cpu")]
    [InlineData("silos", "0", "8Gi", 1, "cpu")]
    [InlineData("silos", "4", "8GB", 1, "memory")]
    [InlineData("silos", "4", "0Gi", 1, "memory")]
    [InlineData("silos", "4", "8Gi", 0, "replicas")]
    public void AMalformedSlot_IsANamedProblem(string pool, string cpu, string memory, int replicas, string field)
    {
        var problems = NodeHeadroomSlot.ProblemsOf([new NodeHeadroomSlot { Pool = pool, Cpu = cpu, Memory = memory, Replicas = replicas }]);
        var problem = Assert.Single(problems);
        Assert.Contains($"nodeHeadroom[0].{field}", problem);
    }

    [Fact]
    public void APoolDeclaredTwice_IsANamedProblem()
    {
        var slot = new NodeHeadroomSlot { Pool = "silos", Cpu = "4", Memory = "8Gi" };
        var problem = Assert.Single(NodeHeadroomSlot.ProblemsOf([slot, slot]));
        Assert.Contains("declared twice", problem);
    }

    [Fact]
    public void TheBlockIsInitOnly_NeverAPositionalConstructor()
    {
        Assert.Contains(typeof(NodeHeadroomSlot).GetConstructors(), c => c.GetParameters().Length == 0);
        Assert.Equal(ImmutableList<NodeHeadroomSlot>.Empty, new DeploymentContent().NodeHeadroom);
    }

    [Fact]
    public void TheFluentMethod_DeclaresAPool_AndReplacesItRatherThanDeclaringItTwice()
    {
        var record = new DeploymentContent()
            .WithNodeHeadroom("silos", "4", "8Gi")
            .WithNodeHeadroom("silos", "6", "12Gi", 2);
        var slot = Assert.Single(record.NodeHeadroom);
        Assert.Equal(("silos", "6", "12Gi", 2), (slot.Pool, slot.Cpu, slot.Memory, slot.Replicas));
        Assert.Empty(NodeHeadroomSlot.ProblemsOf(record.NodeHeadroom));
    }
}
