using System.Collections.Generic;
using System.Text.Json;
using MeshWeaver.Social;
using Xunit;

namespace MeshWeaver.Social.Test;

/// <summary>
/// The profile-identity merge: LinkedIn owns the display name and photo, the mesh owns the rest.
/// A wholesale write here would silently erase a profile's network/headline/owner — which is
/// exactly the class of loss these cases pin.
/// </summary>
public class LinkedInIdentitySyncTest
{
    private static Dictionary<string, object?> Existing() => new()
    {
        ["$type"] = "SocialProfile",
        ["network"] = "LinkedIn",
        ["displayName"] = "Stale Name",
        ["headline"] = "Founder",
        ["owner"] = "Roland",
        ["imageUrl"] = "https://old/photo.jpg",
    };

    [Fact]
    public void Merge_AppliesLinkedInNameAndPhoto()
    {
        var merged = LinkedInIdentitySync.Merge(Existing(), "Roland Bürgi", "https://media.licdn.com/real.jpg");

        merged["displayName"].Should().Be("Roland Bürgi");
        merged["imageUrl"].Should().Be("https://media.licdn.com/real.jpg");
    }

    [Fact]
    public void Merge_KeepsEverythingTheMeshOwns()
    {
        var merged = LinkedInIdentitySync.Merge(Existing(), "Roland Bürgi", "https://media.licdn.com/real.jpg");

        merged["network"].Should().Be("LinkedIn");
        merged["headline"].Should().Be("Founder");
        merged["owner"].Should().Be("Roland");
        merged["$type"].Should().Be("SocialProfile");
    }

    [Fact]
    public void Merge_BlankIncomingValuesNeverErase()
    {
        // An account that exposes no photo must not wipe a good one.
        var merged = LinkedInIdentitySync.Merge(Existing(), null, "   ");

        merged["displayName"].Should().Be("Stale Name");
        merged["imageUrl"].Should().Be("https://old/photo.jpg");
    }

    [Fact]
    public void Merge_FromJsonElementContent()
    {
        using var doc = JsonDocument.Parse("""
            {"$type":"SocialProfile","network":"LinkedIn","headline":"Founder","handle":"roland-buergi"}
            """);
        var merged = LinkedInIdentitySync.Merge(doc.RootElement, "Roland Bürgi", "https://media.licdn.com/real.jpg");

        merged["handle"].Should().Be("roland-buergi");
        merged["headline"].Should().Be("Founder");
        merged["displayName"].Should().Be("Roland Bürgi");
    }

    [Fact]
    public void Merge_FromNullContentStillCarriesTheIdentity()
    {
        var merged = LinkedInIdentitySync.Merge(null, "Roland Bürgi", "https://media.licdn.com/real.jpg");

        merged["$type"].Should().Be("SocialProfile");
        merged["displayName"].Should().Be("Roland Bürgi");
        merged["imageUrl"].Should().Be("https://media.licdn.com/real.jpg");
    }

    [Fact]
    public void ToDictionary_TypedRecordRoundTripsCamelCase()
    {
        var merged = LinkedInIdentitySync.ToDictionary(new { Network = "X", DisplayName = "Systemorph" });

        // The mesh stores camelCase; PascalCase keys would make every reader miss the value.
        merged.Should().ContainKey("network");
        merged.Should().ContainKey("displayName");
    }
}
