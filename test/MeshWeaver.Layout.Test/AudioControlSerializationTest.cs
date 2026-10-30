using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Round-trips <see cref="AudioControl"/> through the hub's polymorphic JSON pipeline: the wire shape
/// carries the SHORT <c>$type</c> discriminator and the renderer-facing property names, and comes back
/// as a typed control — including a bound source, the shape a template column uses.
/// </summary>
public class AudioControlSerializationTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
    {
        return base.ConfigureClient(configuration).AddLayoutTypes();
    }

    [Fact]
    public void AudioControl_RoundTrips_WithShortTypeDiscriminator()
    {
        var client = GetClient();
        var control = Controls.Audio("https://cdn.example.com/preview.m4a")
            .WithTitle("Preview — Track 1")
            .WithPreload(AudioPreload.None);

        var serialized = JsonSerializer.Serialize<UiControl>(control, client.JsonSerializerOptions);

        using (var doc = JsonDocument.Parse(serialized))
        {
            doc.RootElement.GetProperty("$type").GetString().Should().Be(nameof(AudioControl));
            // The renderers read these exact (camelCase) names.
            doc.RootElement.GetProperty("src").GetString().Should().Be("https://cdn.example.com/preview.m4a");
            doc.RootElement.GetProperty("title").GetString().Should().Be("Preview — Track 1");
            doc.RootElement.GetProperty("preload").GetString().Should().Be("none");
        }

        var audio = JsonSerializer.Deserialize<UiControl>(serialized, client.JsonSerializerOptions)
            .Should().BeOfType<AudioControl>().Subject;
        audio.Src.ToString().Should().Be("https://cdn.example.com/preview.m4a");
        audio.Title!.ToString().Should().Be("Preview — Track 1");
        audio.Preload.ToString().Should().Be(AudioPreload.None);
    }

    [Fact]
    public void AudioControl_DefaultsToMetadataPreload_AndNoTitle()
    {
        var client = GetClient();
        var serialized = JsonSerializer.Serialize<UiControl>(
            Controls.Audio("/api/content/clip.mp3"), client.JsonSerializerOptions);

        var audio = JsonSerializer.Deserialize<UiControl>(serialized, client.JsonSerializerOptions)
            .Should().BeOfType<AudioControl>().Subject;
        audio.Preload.ToString().Should().Be(AudioPreload.Metadata);
        audio.Title.Should().BeNull();
    }

    [Fact]
    public void AudioControl_BoundSource_RoundTripsAsAReference()
    {
        var client = GetClient();
        var control = Controls.Audio(new JsonPointerReference("previewUrl"));

        var serialized = JsonSerializer.Serialize<UiControl>(control, client.JsonSerializerOptions);
        var audio = JsonSerializer.Deserialize<UiControl>(serialized, client.JsonSerializerOptions)
            .Should().BeOfType<AudioControl>().Subject;

        audio.Src.Should().BeOfType<JsonPointerReference>()
            .Which.Pointer.Should().Be("previewUrl");
    }
}
