using System.Collections.Immutable;
using MeshWeaver.Deployment;
using Xunit;

namespace MeshWeaver.Deployment.Contract.Test;

/// <summary>
/// The record's <c>portalNodeSelector</c> (Systemorph/Memex#148): the node labels the portal pods must
/// land on, rendered by the chart as the pod spec's <c>nodeSelector</c>. Pinned here: the map round-trips
/// the record's JSON; an absent map reads as null and is written as NOTHING, so a record that does not
/// state it serialises byte-identically to one written before the field existed; and the fluent method
/// sets one label without dropping the others.
/// </summary>
public class PortalNodeSelectorRecordTest
{
    [Fact]
    public void TheRecordsJson_CarriesTheSelector_AndWritesItBack()
    {
        var record = DeploymentRecordJson.Read("""
            { "$type": "DeploymentContent", "host": "x.example.com", "portalNodeSelector": { "workload": "silos" } }
            """)!;

        Assert.Equal(ImmutableSortedDictionary<string, string>.Empty.Add("workload", "silos"), record.PortalNodeSelector);

        var reread = DeploymentRecordJson.Read(DeploymentRecordJson.Write(record))!;
        Assert.Equal(record.PortalNodeSelector, reread.PortalNodeSelector);
    }

    [Fact]
    public void AnAbsentSelector_IsNull_AndIsNotWritten()
    {
        var record = DeploymentRecordJson.Read("""{ "$type": "DeploymentContent", "host": "x.example.com" }""")!;

        Assert.Null(record.PortalNodeSelector);
        Assert.DoesNotContain("portalNodeSelector", DeploymentRecordJson.Write(record));
    }

    [Fact]
    public void TheFluentMethod_SetsOneLabel_AndKeepsTheOthers()
    {
        var record = new DeploymentContent()
            .WithPortalNodeSelector("workload", "silos")
            .WithPortalNodeSelector("zone", "a")
            .WithPortalNodeSelector("workload", "portal");

        Assert.Equal(
            ImmutableSortedDictionary<string, string>.Empty.Add("workload", "portal").Add("zone", "a"),
            record.PortalNodeSelector);
    }
}
