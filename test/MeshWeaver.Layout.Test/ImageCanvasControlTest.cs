using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// The <see cref="ImageCanvasControl"/> contract: what the factory builds, that the fluent API is
/// immutable and keeps bindings, that the control survives the wire unchanged (the renderers read
/// exactly these camelCase properties), and the crop geometry every renderer must apply
/// (<see cref="ImageCanvasCrop.Normalize"/>).
/// </summary>
public class ImageCanvasControlTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration).AddLayout(x => x);

    [Fact]
    public void Factory_SetsImageAndAlt_AndBindsNoCrop()
    {
        var canvas = Controls.ImageCanvas("data:image/png;base64,AAAA", "A screenshot");

        canvas.Image.Should().Be("data:image/png;base64,AAAA");
        canvas.Alt.Should().Be("A screenshot");
        canvas.HasCrop.Should().BeFalse("no trim is bound, so the renderer shows no crop tool");
        canvas.MaxHeight.Should().BeNull("the renderer applies DefaultMaxHeight when none is authored");
        ImageCanvasControl.DefaultMaxHeight.Should().Be("70vh");
    }

    [Fact]
    public void FluentSurface_IsImmutable_AndKeepsBindings()
    {
        var canvas = Controls.ImageCanvas("x.png", "");
        var bound = canvas
            .WithModelSize(416, 892)
            .WithCrop(new JsonPointerReference("cropLeft"), new JsonPointerReference("cropTop"),
                new JsonPointerReference("cropRight"), new JsonPointerReference("cropBottom"))
            .WithMaxHeight("60vh")
            .WithAlt("Map");

        canvas.HasCrop.Should().BeFalse("the original is untouched");
        canvas.ModelWidth.Should().BeNull();
        bound.HasCrop.Should().BeTrue();
        bound.ModelWidth.Should().Be(416);
        bound.ModelHeight.Should().Be(892);
        bound.CropRight.Should().BeOfType<JsonPointerReference>().Which.Pointer.Should().Be("cropRight");
        bound.MaxHeight.Should().Be("60vh");
        bound.Alt.Should().Be("Map");
    }

    [HubFact]
    public void RoundTrip_KeepsEveryProperty_CamelCased()
    {
        var host = GetHost();
        var canvas = Controls.ImageCanvas(new JsonPointerReference("image"), "Map")
            .WithModelSize(416, 892)
            .WithCrop(new JsonPointerReference("cropLeft"), new JsonPointerReference("cropTop"),
                new JsonPointerReference("cropRight"), new JsonPointerReference("cropBottom"))
            .WithMaxHeight("60vh");

        var json = JsonSerializer.Serialize<UiControl>(canvas, host.JsonSerializerOptions);
        Output.WriteLine(json);
        json.Should().Contain("\"$type\":\"ImageCanvasControl\"");
        json.Should().Contain("\"modelWidth\":416").And.Contain("\"modelHeight\":892")
            .And.Contain("\"cropLeft\"").And.Contain("\"cropBottom\"").And.Contain("\"maxHeight\":\"60vh\"");

        var back = JsonSerializer.Deserialize<UiControl>(json, host.JsonSerializerOptions)
            .Should().BeOfType<ImageCanvasControl>().Which;
        back.Image.Should().BeOfType<JsonPointerReference>().Which.Pointer.Should().Be("image");
        back.CropTop.Should().BeOfType<JsonPointerReference>().Which.Pointer.Should().Be("cropTop");
        back.Should().Be(canvas, "records compare by value, so a lossless round trip is equality");
    }

    [Fact]
    public void Normalize_LeavesAValidCropUntouched()
    {
        var crop = new ImageCanvasCrop(10, 20, 30, 40, 400, 300);
        crop.Normalize().Should().Be(crop);
    }

    [Fact]
    public void Normalize_ClampsNegativeAndOversizedTrims()
    {
        new ImageCanvasCrop(-5, -1, 0, 0, 400, 300).Normalize()
            .Should().Be(new ImageCanvasCrop(0, 0, 0, 0, 400, 300), "a negative trim is no trim");
        new ImageCanvasCrop(1000, 0, 0, 0, 400, 300).Normalize()
            .Should().Be(new ImageCanvasCrop(399, 0, 0, 0, 400, 300), "one unit always remains");
    }

    [Fact]
    public void Normalize_TheDraggedEdgeGivesWay_NotTheOtherOne()
    {
        // Dragging the LEFT edge past the right one stops at the right edge: the right trim keeps
        // its value and the left trim shrinks so exactly one unit remains.
        new ImageCanvasCrop(300, 0, 150, 0, 400, 300).Normalize(ImageCanvasEdge.Left)
            .Should().Be(new ImageCanvasCrop(249, 0, 150, 0, 400, 300));
        // Dragging the RIGHT edge (or no edge) past the left one: the right trim gives way.
        new ImageCanvasCrop(300, 0, 150, 0, 400, 300).Normalize(ImageCanvasEdge.Right)
            .Should().Be(new ImageCanvasCrop(300, 0, 99, 0, 400, 300));
        new ImageCanvasCrop(0, 250, 0, 100, 400, 300).Normalize()
            .Should().Be(new ImageCanvasCrop(0, 250, 0, 49, 400, 300));
    }

    [Fact]
    public void Normalize_ADegenerateExtentIsTreatedAsOneUnit()
    {
        new ImageCanvasCrop(5, 5, 5, 5, 0, -3).Normalize()
            .Should().Be(new ImageCanvasCrop(0, 0, 0, 0, 1, 1));
    }
}
