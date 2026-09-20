using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// The <see cref="SignaturePadControl"/> contract: what the factory builds, what the defaults are,
/// and that the control survives the wire unchanged — the Blazor renderer reads exactly these
/// camelCase properties, so a renamed or dropped one is a pad that silently renders with the
/// defaults (the #866 shape: a property that is correct and ignored).
/// </summary>
public class SignaturePadControlTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration).AddLayout(x => x);

    [Fact]
    public void Factory_BindsTheDataAndAppliesTheDefaults()
    {
        var pad = Controls.SignaturePad("/data/signature");

        pad.Data.Should().Be("/data/signature");
        pad.Width.Should().BeNull("the renderer applies DefaultWidth when none is authored");
        pad.Height.Should().BeNull("the renderer applies DefaultHeight when none is authored");
        SignaturePadControl.DefaultWidth.Should().Be(480);
        SignaturePadControl.DefaultHeight.Should().Be(160);
        pad.PenColor.Should().Be(SignaturePadControl.DefaultPenColor);
        pad.ClearButton.Should().Be(true);
        pad.Placeholder.Should().BeNull("the renderer localizes the hint when none is authored");
    }

    [Fact]
    public void FluentSurface_IsImmutable()
    {
        var pad = Controls.SignaturePad("");
        var sized = pad.WithWidth(640).WithHeight(200).WithPenColor("#000").WithClearButton(false)
            .WithPlaceholder("Unterschrift").WithLabel("Signature");

        pad.Width.Should().BeNull("the original is untouched");
        sized.Width.Should().Be(640);
        sized.Height.Should().Be(200);
        sized.PenColor.Should().Be("#000");
        sized.ClearButton.Should().Be(false);
        sized.Placeholder.Should().Be("Unterschrift");
        sized.Label.Should().Be("Signature");
    }

    [HubFact]
    public void RoundTrip_KeepsEveryProperty_CamelCased()
    {
        var host = GetHost();
        var pad = Controls.SignaturePad(new JsonPointerReference("signature"))
            .WithWidth(600).WithHeight(180).WithPenColor("#123456").WithClearButton(false)
            .WithPlaceholder("Sign here").WithAriaLabel("Signature pad");

        var json = JsonSerializer.Serialize<UiControl>(pad, host.JsonSerializerOptions);
        Output.WriteLine(json);
        json.Should().Contain("\"$type\":\"SignaturePadControl\"");
        json.Should().Contain("\"width\":600").And.Contain("\"height\":180")
            .And.Contain("\"penColor\":\"#123456\"").And.Contain("\"clearButton\":false");

        var back = JsonSerializer.Deserialize<UiControl>(json, host.JsonSerializerOptions);
        var pad2 = back.Should().BeOfType<SignaturePadControl>().Which;
        pad2.Data.Should().BeOfType<JsonPointerReference>().Which.Pointer.Should().Be("signature");
        pad2.Placeholder.Should().Be("Sign here");
        pad2.AriaLabel.Should().Be("Signature pad");
        pad2.Should().Be(pad, "records compare by value, so a lossless round trip is equality");
    }

    [Fact]
    public void ClearButton_CarriesABinding_ThroughTheFluentApi_AndSurvivesTheRoundTrip()
    {
        // The property is declared object? so it can hold a JsonPointerReference; the fluent
        // setter must not narrow that to bool, or a binding is unreachable through the API
        // (Copilot review, MeshWeaver#4982).
        var bound = Controls.SignaturePad("/data/signature").WithClearButton(new JsonPointerReference("/data/offerClear"));
        bound.ClearButton.Should().BeOfType<JsonPointerReference>()
            .Which.Pointer.Should().Be("/data/offerClear");

        var host = GetHost();
        var json = JsonSerializer.Serialize<UiControl>(bound, host.JsonSerializerOptions);
        var back = JsonSerializer.Deserialize<UiControl>(json, host.JsonSerializerOptions)
            .Should().BeOfType<SignaturePadControl>().Subject;
        back.ClearButton.Should().BeOfType<JsonPointerReference>()
            .Which.Pointer.Should().Be("/data/offerClear");

        Controls.SignaturePad("").WithClearButton().ClearButton.Should().Be(true);
    }
}
