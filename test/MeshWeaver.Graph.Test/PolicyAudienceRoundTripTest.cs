using System.Text.Json;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// <see cref="PartitionAccessPolicy.Audience"/> is DATA the partition's owner reads (the Store's
/// plan coverage). It must survive a typed round-trip of the policy — the shape a hub that knows
/// the type materialises — and an absent list must stay absent, so every existing policy keeps
/// its stored bytes.
/// </summary>
public class PolicyAudienceRoundTripTest
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Audience_SurvivesATypedRoundTrip()
    {
        var policy = new PartitionAccessPolicy
        {
            RedirectOnDenied = "Underwriting/Subscribe",
            Audience = ["Groups/Underwriters", "max.muster"],
        };
        var json = JsonSerializer.Serialize(policy, Web);
        var back = JsonSerializer.Deserialize<PartitionAccessPolicy>(json, Web);
        Assert.NotNull(back);
        Assert.Equal(["Groups/Underwriters", "max.muster"], back!.Audience);
        Assert.Equal("Underwriting/Subscribe", back.RedirectOnDenied);
    }

    [Fact]
    public void AStoredPolicyWithoutAudience_ReadsAsNoNarrowing()
    {
        var back = JsonSerializer.Deserialize<PartitionAccessPolicy>(
            """{"redirectOnDenied":"Underwriting/Subscribe"}""", Web);
        Assert.NotNull(back);
        Assert.Null(back!.Audience);
    }

    [Fact]
    public void Audience_DoesNotChangeThePermissionCap()
    {
        var open = new PartitionAccessPolicy();
        var narrowed = new PartitionAccessPolicy { Audience = ["Groups/Underwriters"] };
        Assert.Equal(open.GetPermissionCap(), narrowed.GetPermissionCap());
    }
}
