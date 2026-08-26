using System.Text.Json;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Social.Test;

/// <summary>
/// The one thing every content write from this compiled module must guarantee: the node's
/// <c>$type</c> discriminator SURVIVES the round trip, so the node still reads as its own type
/// (issue #52).
///
/// <para>🚨 <b>Why this needs a real hub, and why the old unit test could not catch the bug.</b>
/// The corruption does not happen in the merge — it happens in the platform's
/// <c>ObjectPolymorphicConverter</c>, on the way to storage. The previous test asserted
/// <c>merged["$type"] == "SocialProfile"</c> on the DICTIONARY the merge returned, which was
/// perfectly true and completely irrelevant: the converter takes the discriminator from the CLR
/// type of the value it is handed and, while copying the payload's properties across, explicitly
/// SKIPS any property called <c>$type</c>. So a dictionary carrying the right discriminator landed
/// in storage carrying the wrong one, under a green test. The assertion therefore has to be made on
/// the SERIALIZED node, through a hub's own <see cref="IMessageHub.JsonSerializerOptions"/> — which
/// is what this fixture provides, with no mock anywhere.</para>
///
/// <para>The hub registers <see cref="SocialProfile"/> and <see cref="SocialPost"/> by SHORT NAME,
/// which is exactly the shape a dynamic NodeType's hub has: the type is declared in the mesh
/// (<c>config.WithContentType&lt;SocialProfile&gt;()</c>) and can only ever be addressed by its
/// discriminator string from a compiled assembly.</para>
/// </summary>
public class NodeContentTypeRoundTripTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureMesh(MessageHubConfiguration conf) =>
        base.ConfigureMesh(conf)
            .WithType<SocialProfile>(nameof(SocialProfile))
            .WithType<SocialPost>(nameof(SocialPost));

    /// <summary>The profile as it is stored before anyone connects LinkedIn.</summary>
    private static SocialProfile StoredProfile => new()
    {
        Network = "LinkedIn",
        DisplayName = "Carson Bryant",
        Headline = "notus",
        Owner = "carson",
        DefaultPublishTime = "08:00+02:00",
    };

    private MeshNode ProfileNode(object? content) =>
        new("CarsonLinkedIn", "Profiles")
        {
            Name = "Carson Bryant — LinkedIn",
            NodeType = "SocialMedia/Profile",
            State = MeshNodeState.Active,
            Content = content,
        };

    /// <summary>Serializes a node with the hub's options and hands back the raw content object.</summary>
    private JsonElement SerializedContent(MeshNode node)
    {
        var json = JsonSerializer.Serialize(node, Mesh.JsonSerializerOptions);
        Output.WriteLine(json);
        using var doc = JsonDocument.Parse(json);
        foreach (var property in doc.RootElement.EnumerateObject())
            if (property.NameEquals("content"))
                return property.Value.Clone();
        Assert.Fail("the serialized node carries no content at all: " + json);
        return default;
    }

    /// <summary>Round-trips a node through the hub's serializer, exactly as a write + read does.</summary>
    private MeshNode RoundTrip(MeshNode node) =>
        JsonSerializer.Deserialize<MeshNode>(
            JsonSerializer.Serialize(node, Mesh.JsonSerializerOptions), Mesh.JsonSerializerOptions)!;

    /// <summary>
    /// 🚨 THE issue-#52 regression test. The identity sync's result must land as a
    /// <c>SocialProfile</c> — not as the CLR name of whatever container carried it.
    /// </summary>
    [Fact]
    public void IdentitySync_KeepsTheProfileTypedThroughAWrite()
    {
        var merged = LinkedInIdentitySync.Merge(
            StoredProfile, "Carson Blaze", "https://media.licdn.com/photo.jpg");

        var content = SerializedContent(ProfileNode(merged));

        Assert.Equal("SocialProfile", content.GetProperty("$type").GetString());

        var stored = RoundTrip(ProfileNode(merged));
        var profile = Assert.IsType<SocialProfile>(stored.Content);
        Assert.Equal("Carson Blaze", profile.DisplayName);
        Assert.Equal("https://media.licdn.com/photo.jpg", profile.ImageUrl);
        // Everything the mesh owns survives — the merge must never be a replace.
        Assert.Equal("LinkedIn", profile.Network);
        Assert.Equal("notus", profile.Headline);
        Assert.Equal("carson", profile.Owner);
        Assert.Equal("08:00+02:00", profile.DefaultPublishTime);
    }

    /// <summary>
    /// The exact production shape: the profile arrives as untyped JSON (a hub that cannot resolve
    /// the discriminator hands back a <see cref="JsonElement"/>), and the merge must still write it
    /// back TYPED rather than degrading it one step further.
    /// </summary>
    [Fact]
    public void IdentitySync_KeepsTheProfileTypedWhenItArrivedAsRawJson()
    {
        using var doc = JsonDocument.Parse(
            """
            {"$type":"SocialProfile","network":"LinkedIn","displayName":"Carson Bryant",
             "headline":"notus","owner":"carson","defaultPublishTime":"08:00+02:00"}
            """);

        var merged = LinkedInIdentitySync.Merge(
            doc.RootElement.Clone(), "Carson Blaze", "https://media.licdn.com/photo.jpg");

        var profile = Assert.IsType<SocialProfile>(RoundTrip(ProfileNode(merged)).Content);
        Assert.Equal("Carson Blaze", profile.DisplayName);
        Assert.Equal("notus", profile.Headline);
    }

    /// <summary>
    /// A profile ALREADY damaged by the defect — <c>$type</c> rewritten to the dictionary's CLR
    /// collection name — is REPAIRED by the next connect rather than having its damage lovingly
    /// preserved. Two members' profiles were in this state when the issue was filed.
    /// </summary>
    [Fact]
    public void IdentitySync_RepairsAProfileAnEarlierBuildDamaged()
    {
        using var damaged = JsonDocument.Parse(
            """
            {"$type":"Dictionary`2[String,Object]","network":"LinkedIn","displayName":"Carson Blaze",
             "headline":"notus","owner":"carson"}
            """);
        // The damage is real: as stored, this node does NOT read as a profile.
        Assert.IsNotType<SocialProfile>(RoundTrip(ProfileNode(damaged.RootElement.Clone())).Content);

        var merged = LinkedInIdentitySync.Merge(damaged.RootElement.Clone(), "Carson Blaze", null);

        var profile = Assert.IsType<SocialProfile>(RoundTrip(ProfileNode(merged)).Content);
        Assert.Equal("carson", profile.Owner);
    }

    /// <summary>
    /// A blank incoming value never erases a good stored one — an account that exposes no photo
    /// must not wipe the profile's avatar.
    /// </summary>
    [Fact]
    public void IdentitySync_BlankIncomingValuesNeverErase()
    {
        var merged = LinkedInIdentitySync.Merge(
            StoredProfile with { ImageUrl = "https://old/photo.jpg" }, null, "   ");

        var profile = Assert.IsType<SocialProfile>(RoundTrip(ProfileNode(merged)).Content);
        Assert.Equal("Carson Bryant", profile.DisplayName);
        Assert.Equal("https://old/photo.jpg", profile.ImageUrl);
    }

    /// <summary>
    /// The publisher's write-back is the SAME defect on the post: every publish and every
    /// engagement refresh used to rebuild the content as a dictionary, so a post lost its type the
    /// moment it was published — the page that shows the result being the first thing to read it.
    /// </summary>
    [Fact]
    public void PublishWriteBack_KeepsThePostTyped()
    {
        var post = new SocialPost
        {
            Text = "Hello world",
            AuthorPath = "Profiles/CarsonLinkedIn",
            Status = "Scheduled",
        };
        var node = new MeshNode("CarsonPublishTest", "Posts")
        {
            NodeType = "SocialMedia/Post",
            Content = NodeContentJson.Merge(post, PostPublishProblem.PostContentType,
            [
                new("status", "Published"),
                new("publishedUrn", "urn:li:share:123"),
            ]),
        };

        var stored = Assert.IsType<SocialPost>(RoundTrip(node).Content);
        Assert.Equal("Published", stored.Status);
        Assert.Equal("urn:li:share:123", stored.PublishedUrn);
        Assert.Equal("Hello world", stored.Text);
        Assert.Equal("Profiles/CarsonLinkedIn", stored.AuthorPath);
    }

    /// <summary>
    /// The failure reason lands on the post as a readable sentence AND survives the typed round
    /// trip — a field the record does not declare is silently dropped, which would leave the page
    /// that must show it reading null (issue #50).
    /// </summary>
    [Fact]
    public void PublishProblem_SurvivesTheTypedRoundTrip()
    {
        var attemptedAt = new DateTimeOffset(2026, 8, 23, 13, 48, 0, TimeSpan.Zero);
        var content = PostPublishProblem.Apply(
            new SocialPost { Text = "Hello", Status = "Scheduled" },
            "not-connected",
            statusCode: 0,
            attemptedAt);
        var node = new MeshNode("CarsonPublishTest", "Posts")
        {
            NodeType = "SocialMedia/Post",
            Content = content,
        };

        // 🚨 The timestamp is stored as the UTC instant with a Z, never with an offset. An offset
        // reads back as DateTimeKind.Local — the same instant re-expressed in the SERVER's zone —
        // and the post page, which labels this "UTC", would then print the wrong wall-clock time.
        Assert.EndsWith("Z\"",
            NodeContentJson.ToJsonObject(content)[PostPublishProblem.AttemptedAtKey]!.ToJsonString(),
            StringComparison.Ordinal);

        var stored = Assert.IsType<SocialPost>(RoundTrip(node).Content);
        Assert.Contains("connect LinkedIn", stored.LastPublishError!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(attemptedAt.UtcDateTime, stored.LastPublishAttemptAt);
        Assert.Equal(DateTimeKind.Utc, stored.LastPublishAttemptAt!.Value.Kind);

        // …and a success CLEARS it, rather than leaving a live post explaining a failure it
        // recovered from.
        var cleared = Assert.IsType<SocialPost>(
            RoundTrip(node with
            {
                Content = PostPublishProblem.Apply(stored, null, statusCode: 0, attemptedAt),
            }).Content);
        Assert.Null(cleared.LastPublishError);
        Assert.Null(cleared.LastPublishAttemptAt);
    }
}

/// <summary>
/// Stand-in for the mesh's <c>SocialMedia/Profile</c> content type. Same SHORT NAME and same
/// properties as <c>SocialMedia/Post/Source/SocialProfile.cs</c>, because the short name is the
/// discriminator and the discriminator is what these tests are about.
/// </summary>
public record SocialProfile
{
    public string? Network { get; init; }
    public string? DisplayName { get; init; }
    public string? Headline { get; init; }
    public string? ImageUrl { get; init; }
    public string? ProfileUrl { get; init; }
    public string? Handle { get; init; }
    public string? Owner { get; init; }
    public string? DefaultPublishTime { get; init; }
}

/// <summary>Stand-in for the mesh's <c>SocialMedia/Post</c> content type — see
/// <see cref="SocialProfile"/>.</summary>
public record SocialPost
{
    public string? Text { get; init; }
    public string? AuthorPath { get; init; }
    public DateTime? ScheduledAt { get; init; }
    public string? Status { get; init; }
    public string? MediaUrl { get; init; }
    public string? PublishedUrl { get; init; }
    public string? PublishedUrn { get; init; }
    public DateTime? PublishedAt { get; init; }
    public string? LastPublishError { get; init; }
    public DateTime? LastPublishAttemptAt { get; init; }
}
