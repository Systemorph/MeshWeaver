using MeshWeaver.Deployment;
using Xunit;

namespace MeshWeaver.Deployment.Contract.Test;

/// <summary>
/// The declared site of a host record and its record-level fleet-jobs switch (Plugins
/// Hosting/MacStudioFleet §2c): a <c>mac-host</c> record reads <c>site</c> and
/// <c>acceptsFleetJobs</c> from its JSON and writes them back unchanged — the date as a calendar
/// date, the coordinates as numbers — and a record that states neither (every record before these
/// fields) reads null and writes no key, so it renders exactly what it rendered before.
/// </summary>
public class HostSiteRecordTest
{
    private const string Sited = """
        {
          "$type": "DeploymentContent",
          "kind": "mac-host",
          "machine": "C02XK0AAJGH7",
          "networkName": "studio-1",
          "acceptsFleetJobs": false,
          "site": {
            "name": "Fabrikam AG",
            "street": "Musterstrasse 1",
            "postalCode": "8000",
            "city": "Zürich",
            "country": "CH",
            "latitude": 47.3769,
            "longitude": 8.5417,
            "since": "2026-11-20",
            "notes": "server room, rack 2"
          }
        }
        """;

    private const string Unsited = """
        {
          "$type": "DeploymentContent",
          "kind": "mac-host",
          "machine": "C02XK0AAJGH8",
          "networkName": "studio-2"
        }
        """;

    [Fact]
    public void ADeclaredSite_RoundTripsEveryField()
    {
        var record = DeploymentRecordJson.Read(Sited)!;
        var site = Assert.IsType<HostSite>(record.Site);
        Assert.Equal("Fabrikam AG", site.Name);
        Assert.Equal("Musterstrasse 1", site.Street);
        Assert.Equal("8000", site.PostalCode);
        Assert.Equal("Zürich", site.City);
        Assert.Equal("CH", site.Country);
        Assert.Equal(47.3769, site.Latitude);
        Assert.Equal(8.5417, site.Longitude);
        Assert.Equal(new DateOnly(2026, 11, 20), site.Since);
        Assert.Equal("server room, rack 2", site.Notes);
        Assert.False(record.AcceptsFleetJobs);

        var written = DeploymentRecordJson.Write(record);
        Assert.Contains("\"site\":{", written);
        Assert.Contains("\"since\":\"2026-11-20\"", written);
        Assert.Contains("\"latitude\":47.3769", written);
        Assert.Contains("\"acceptsFleetJobs\":false", written);

        var again = DeploymentRecordJson.Read(written)!;
        Assert.Equal(record.Site, again.Site);
        Assert.Equal(record.AcceptsFleetJobs, again.AcceptsFleetJobs);
    }

    [Fact]
    public void ARecordDeclaringNoSite_ReadsNull_AndWritesNoKey()
    {
        var record = DeploymentRecordJson.Read(Unsited)!;
        Assert.Null(record.Site);
        Assert.Null(record.AcceptsFleetJobs);

        var written = DeploymentRecordJson.Write(record);
        Assert.DoesNotContain("\"site\"", written);
        Assert.DoesNotContain("acceptsFleetJobs", written);
        Assert.Equal(written, DeploymentRecordJson.Write(DeploymentRecordJson.Read(written)!));
    }

    [Fact]
    public void APartialSite_KeepsWhatItStates_AndLeavesTheRestNull()
    {
        var record = DeploymentRecordJson.Read("""{ "kind": "mac-host", "site": { "city": "Zürich", "country": "CH" } }""")!;
        Assert.Equal(new HostSite { City = "Zürich", Country = "CH" }, record.Site);
        var site = System.Text.Json.Nodes.JsonNode.Parse(DeploymentRecordJson.Write(record))!["site"]!.AsObject();
        Assert.Equal(new[] { "city", "country" }, site.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("Zürich", (string?)site["city"]);
    }

    [Fact]
    public void TheNewFieldsAreInitProperties_TheConstructorSignatureIsUnchanged()
    {
        Assert.NotNull(typeof(DeploymentContent).GetConstructor(Type.EmptyTypes));
        foreach (var name in new[] { nameof(DeploymentContent.Site), nameof(DeploymentContent.AcceptsFleetJobs) })
        {
            var setter = typeof(DeploymentContent).GetProperty(name)!.SetMethod!;
            Assert.Contains(setter.ReturnParameter.GetRequiredCustomModifiers(), m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
        }
    }
}
