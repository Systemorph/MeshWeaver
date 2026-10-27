using System;
using System.Collections.Immutable;
using System.Text.Json;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The telemetry an instance carries on its token exchange is ADDITIVE (Doc/Architecture/InstanceTelemetry):
/// a consumer that predates the field sends the body it always sent and the registry reads it exactly
/// as before; a consumer that knows the field sends it and it round-trips; every field is bounded; and
/// the package summary is a count plus a digest — order-independent and carrying no package name.
/// </summary>
public class InstanceTelemetryPayloadTest
{
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"scope":["Plugins/*"],"lifetimeSeconds":600}""")]
    [InlineData("""{"scope":null,"lifetimeSeconds":null}""")]
    public void AnOldConsumersBody_ReadsAsItAlwaysDid_WithNoTelemetry(string body)
    {
        var request = JsonSerializer.Deserialize<SyncTokenPayloads.Request>(body, SyncTokenPayloads.Json);

        request.Should().NotBeNull();
        request!.Telemetry.Should().BeNull("an absent field is an old consumer, not an empty report");
        if (body.Contains("Plugins/*", StringComparison.Ordinal))
        {
            request.Scope.Should().Equal(["Plugins/*"]);
            request.LifetimeSeconds.Should().Be(600);
        }
    }

    [Fact]
    public void ANewConsumersBody_RoundTripsItsTelemetry()
    {
        var sent = new SyncTokenPayloads.Request(
            Telemetry: new InstanceTelemetry("3.1.4123+abc", "0123456789abcdef", "https://acme.example", "87 packages · 3f9a1c2e"));

        var wire = JsonSerializer.Serialize(sent, SyncTokenPayloads.Json);
        var read = JsonSerializer.Deserialize<SyncTokenPayloads.Request>(wire, SyncTokenPayloads.Json);

        wire.Should().Contain("\"telemetry\"", "camelCase on the wire, like every other field");
        read!.Telemetry.Should().Be(sent.Telemetry);
        read.Scope.Should().BeNull();
        read.LifetimeSeconds.Should().BeNull();
    }

    [Fact]
    public void Bounded_TrimsBlanksToNull_AndCutsOverlongFields()
    {
        var overlong = new string('v', InstanceTelemetry.MaxFieldLength + 50);
        var telemetry = new InstanceTelemetry("  3.1.0  ", "   ", null, overlong).Bounded();

        telemetry.PlatformVersion.Should().Be("3.1.0");
        telemetry.Commit.Should().BeNull("a blank is nothing reported, not an empty string to stamp");
        telemetry.HomeUrl.Should().BeNull();
        telemetry.Summary!.Length.Should().Be(InstanceTelemetry.MaxFieldLength);
        new InstanceTelemetry().IsEmpty.Should().BeTrue();
        new InstanceTelemetry(" ", " ", " ", " ").IsEmpty.Should().BeTrue();
        telemetry.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public void Summarize_IsACountAndADigest_OrderIndependent_NamingNoPackage()
    {
        var a = new ModuleCoordinate { ModuleId = "Store", Package = new PackageCoordinate { PackageId = "Store", Version = "1.4.2" } };
        var b = new ModuleCoordinate { ModuleId = "Edu", GitSync = new GitSyncCoordinate { LastSyncCommitSha = "deadbeef" } };
        var c = new ModuleCoordinate { ModuleId = "Publish", Package = new PackageCoordinate { PackageId = "Publish", Version = "2.0.0" } };

        var oneWay = InstanceTelemetryReader.Summarize(new InstanceCombo { Modules = [a, b, c] });
        var otherWay = InstanceTelemetryReader.Summarize(new InstanceCombo { Modules = [c, a, b] });
        var changed = InstanceTelemetryReader.Summarize(new InstanceCombo
        {
            Modules = [a, b, c with { Package = new PackageCoordinate { PackageId = "Publish", Version = "2.0.1" } }],
        });

        oneWay.Should().Be(otherWay, "the digest is over the SORTED set");
        oneWay.Should().StartWith("3 packages · ");
        oneWay.Should().NotContain("Store").And.NotContain("Edu", "a summary names no package");
        oneWay.Split(" · ")[1].Should().MatchRegex("^[0-9a-f]{8}$");
        changed.Should().NotBe(oneWay, "a version change anywhere changes the digest");
        InstanceTelemetryReader.Summarize(new InstanceCombo { Modules = [a] }).Should().StartWith("1 package · ");
        InstanceTelemetryReader.Summarize(new InstanceCombo()).Should().StartWith("0 packages · ");
    }
}
