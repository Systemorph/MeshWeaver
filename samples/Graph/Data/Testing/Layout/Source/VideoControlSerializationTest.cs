// <meshweaver>
// Id: Testing/Layout/VideoControlSerializationTest
// DisplayName: Testing/Layout/VideoControlSerializationTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System;
using System.Text.Json;
using MeshWeaver.Messaging;

/// <summary>
/// Round-trips <see cref="VideoControl"/> through the hub's polymorphic JSON
/// pipeline: the wire shape must carry the SHORT <c>$type</c> discriminator
/// (<c>VideoControl</c>, via the reflective <c>AddLayoutTypes</c> sweep) and
/// deserialize back to a typed control with every property intact.
/// </summary>
public class VideoControlSerializationTest(MeshTestContext context) : InMeshTestBase(output)
{
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration).AddLayoutTypes();
    }

    [MeshFact]
    public void VideoControl_RoundTrips_WithShortTypeDiscriminator()
    {
        var client = GetClient();
        var control = Controls.Video("https://cdn.example.com/lecture-1.mp4")
            .WithPoster("https://cdn.example.com/lecture-1.jpg")
            .WithTitle("Lecture 1 — Introduction")
            .WithAspectRatio("4/3");

        var serialized = JsonSerializer.Serialize<UiControl>(control, client.JsonSerializerOptions);

        // Short $type — never the full CLR name on the wire.
        using (var doc = JsonDocument.Parse(serialized))
        {
            doc.RootElement.GetProperty("$type").GetString().Should().Be(nameof(VideoControl));
        }

        var deserialized = JsonSerializer.Deserialize<UiControl>(serialized, client.JsonSerializerOptions);
        var video = deserialized.Should().BeOfType<VideoControl>().Subject;
        video.Src.ToString().Should().Be("https://cdn.example.com/lecture-1.mp4");
        video.Poster!.ToString().Should().Be("https://cdn.example.com/lecture-1.jpg");
        video.Title!.ToString().Should().Be("Lecture 1 — Introduction");
        video.Kind.Should().Be(VideoKind.Video);
        video.AspectRatio.ToString().Should().Be("4/3");
    }

    [MeshFact]
    public void VideoControl_EmbedKind_RoundTrips()
    {
        var client = GetClient();
        var control = Controls.Video("https://www.youtube-nocookie.com/embed/abc123")
            .WithKind(VideoKind.Embed);

        var serialized = JsonSerializer.Serialize<UiControl>(control, client.JsonSerializerOptions);
        var video = JsonSerializer.Deserialize<UiControl>(serialized, client.JsonSerializerOptions)
            .Should().BeOfType<VideoControl>().Subject;
        video.Kind.Should().Be(VideoKind.Embed);
        video.AspectRatio.ToString().Should().Be("16/9");
    }
}
