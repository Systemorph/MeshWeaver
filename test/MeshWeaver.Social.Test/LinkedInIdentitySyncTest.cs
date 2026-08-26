using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace MeshWeaver.Social.Test;

/// <summary>
/// The profile-identity MERGE rules: LinkedIn owns the display name and photo, the mesh owns the
/// rest. A wholesale write here would silently erase a profile's network/headline/owner — which is
/// exactly the class of loss these cases pin.
///
/// <para>🚨 <b>What this file may NOT be trusted to prove (issue #52).</b> These cases assert on
/// the object the merge RETURNS, and that is the assertion style under which the <c>$type</c>
/// destruction shipped green: the predecessor of this file checked
/// <c>merged["$type"] == "SocialProfile"</c> on the <c>Dictionary</c> the merge produced, which was
/// perfectly true and completely irrelevant — the discriminator was destroyed later, by the
/// platform's <c>ObjectPolymorphicConverter</c>, on the way to storage. Every discriminator and
/// round-trip assertion therefore lives in <see cref="NodeContentTypeRoundTripTest"/>, where a REAL
/// hub's <c>JsonSerializerOptions</c> does the serializing. What is left here is what an in-memory
/// assertion can honestly cover: which FIELDS survive a merge.</para>
/// </summary>
public class LinkedInIdentitySyncTest
{
    private static JsonObject Existing() => JsonNode.Parse(
        """
        {"$type":"SocialProfile","network":"LinkedIn","displayName":"Stale Name",
         "headline":"Founder","owner":"Roland","imageUrl":"https://old/photo.jpg"}
        """)!.AsObject();

    private static string? Text(JsonObject content, string key) =>
        content.TryGetPropertyValue(key, out var value) && value?.GetValueKind() == JsonValueKind.String
            ? value.GetValue<string>()
            : null;

    [Fact]
    public void Merge_AppliesLinkedInNameAndPhoto()
    {
        var merged = LinkedInIdentitySync.Merge(Existing(), "Roland Bürgi", "https://media.licdn.com/real.jpg");

        Text(merged, "displayName").Should().Be("Roland Bürgi");
        Text(merged, "imageUrl").Should().Be("https://media.licdn.com/real.jpg");
    }

    [Fact]
    public void Merge_KeepsEverythingTheMeshOwns()
    {
        var merged = LinkedInIdentitySync.Merge(Existing(), "Roland Bürgi", "https://media.licdn.com/real.jpg");

        Text(merged, "network").Should().Be("LinkedIn");
        Text(merged, "headline").Should().Be("Founder");
        Text(merged, "owner").Should().Be("Roland");
    }

    /// <summary>The caller's stored content must not be mutated by the merge — it is read, not owned.</summary>
    [Fact]
    public void Merge_DoesNotMutateTheStoredContent()
    {
        var stored = Existing();

        LinkedInIdentitySync.Merge(stored, "Roland Bürgi", "https://media.licdn.com/real.jpg");

        Text(stored, "displayName").Should().Be("Stale Name");
        Text(stored, "imageUrl").Should().Be("https://old/photo.jpg");
    }

    [Fact]
    public void Merge_BlankIncomingValuesNeverErase()
    {
        // An account that exposes no photo must not wipe a good one.
        var merged = LinkedInIdentitySync.Merge(Existing(), null, "   ");

        Text(merged, "displayName").Should().Be("Stale Name");
        Text(merged, "imageUrl").Should().Be("https://old/photo.jpg");
    }

    [Fact]
    public void Merge_FromJsonElementContent()
    {
        using var doc = JsonDocument.Parse("""
            {"$type":"SocialProfile","network":"LinkedIn","headline":"Founder","handle":"roland-buergi"}
            """);
        var merged = LinkedInIdentitySync.Merge(doc.RootElement, "Roland Bürgi", "https://media.licdn.com/real.jpg");

        Text(merged, "handle").Should().Be("roland-buergi");
        Text(merged, "headline").Should().Be("Founder");
        Text(merged, "displayName").Should().Be("Roland Bürgi");
    }

    /// <summary>
    /// A fresh profile — nothing stored yet — still carries the identity AND names its type. This
    /// is the one place the fallback discriminator is the only one available; that it then survives
    /// the write is <see cref="NodeContentTypeRoundTripTest"/>'s job.
    /// </summary>
    [Fact]
    public void Merge_FromNullContentStillCarriesTheIdentity()
    {
        var merged = LinkedInIdentitySync.Merge(null, "Roland Bürgi", "https://media.licdn.com/real.jpg");

        Text(merged, "$type").Should().Be(LinkedInIdentitySync.ProfileContentType);
        Text(merged, "displayName").Should().Be("Roland Bürgi");
        Text(merged, "imageUrl").Should().Be("https://media.licdn.com/real.jpg");
    }

    /// <summary>
    /// A dictionary reaching the merge is read like any other shape — this is how a profile written
    /// by an earlier build arrives — and the CLR collection name such a build stamped as
    /// <c>$type</c> is DISCARDED rather than carefully preserved, so the next connect repairs the
    /// node instead of re-damaging it.
    /// </summary>
    [Fact]
    public void Merge_FromADictionaryRepairsTheDamagedDiscriminator()
    {
        var damaged = new Dictionary<string, object?>
        {
            ["$type"] = "Dictionary`2[String,Object]",
            ["network"] = "LinkedIn",
            ["owner"] = "Roland",
        };

        var merged = LinkedInIdentitySync.Merge(damaged, "Roland Bürgi", null);

        Text(merged, "$type").Should().Be(LinkedInIdentitySync.ProfileContentType);
        Text(merged, "owner").Should().Be("Roland");
    }

    [Fact]
    public void ToJsonObject_TypedRecordRoundTripsCamelCase()
    {
        var content = NodeContentJson.ToJsonObject(new { Network = "X", DisplayName = "Systemorph" });

        // The mesh stores camelCase; PascalCase keys would make every reader miss the value.
        content.ContainsKey("network").Should().BeTrue();
        content.ContainsKey("displayName").Should().BeTrue();
        Text(content, "displayName").Should().Be("Systemorph");
    }
}
