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
    public void Normalize_ACornerDragMovesTwoEdges_BothGiveWay()
    {
        // The top-left corner dragged past the bottom-right one: on BOTH axes the dragged edge
        // stops at the opposite one, and the opposite trims keep their values.
        new ImageCanvasCrop(300, 250, 150, 100, 400, 300).Normalize(ImageCanvasEdge.Top, ImageCanvasEdge.Left)
            .Should().Be(new ImageCanvasCrop(249, 199, 150, 100, 400, 300));
        new ImageCanvasCrop(300, 0, 150, 0, 400, 300).Normalize("NotAnEdge")
            .Should().Be(new ImageCanvasCrop(300, 0, 99, 0, 400, 300), "an unknown value is no moving edge");
    }

    [Fact]
    public void Normalize_ADegenerateExtentIsTreatedAsOneUnit()
    {
        new ImageCanvasCrop(5, 5, 5, 5, 0, -3).Normalize()
            .Should().Be(new ImageCanvasCrop(0, 0, 0, 0, 1, 1));
    }

    [HubFact]
    public void WithShapes_BindsShapesToolAndTemplate_AndSurvivesTheWire()
    {
        var canvas = Controls.ImageCanvas("x.png", "")
            .WithModelSize(400, 300)
            .WithShapes(new JsonPointerReference("annotations"), ImageCanvasTool.Arrow,
                new { color = "#e11d48", strokeWidth = 4 });

        canvas.HasShapes.Should().BeTrue();
        canvas.HasCrop.Should().BeFalse("shapes and crop are independent tools");
        Controls.ImageCanvas("x.png", "").HasShapes.Should().BeFalse("no list bound, so no shape tools");

        var host = GetHost();
        var json = JsonSerializer.Serialize<UiControl>(canvas, host.JsonSerializerOptions);
        Output.WriteLine(json);
        json.Should().Contain("\"shapes\"").And.Contain("\"tool\":\"Arrow\"").And.Contain("\"newShape\"");
        var back = JsonSerializer.Deserialize<UiControl>(json, host.JsonSerializerOptions)
            .Should().BeOfType<ImageCanvasControl>().Which;
        back.Shapes.Should().BeOfType<JsonPointerReference>().Which.Pointer.Should().Be("annotations");
        back.Tool.Should().Be(ImageCanvasTool.Arrow);
    }

    /// <summary>
    /// Situation: a canvas with shapes and a bound selection. Action: serialise and read it back.
    /// Expected: the selection binding survives, and a canvas without one has none. Why: the page
    /// reads the selected index to edit that shape's properties; a dropped binding would leave the
    /// selection invisible to it again.
    /// </summary>
    [HubFact]
    public void WithSelection_BindsTheSelectedIndex_AndSurvivesTheWire()
    {
        var canvas = Controls.ImageCanvas("x.png", "")
            .WithModelSize(400, 300)
            .WithShapes(new JsonPointerReference("annotations"), ImageCanvasTool.Select)
            .WithSelection(new JsonPointerReference("selected"));
        Controls.ImageCanvas("x.png", "").Selected.Should().BeNull("no selection bound unless asked for");

        var host = GetHost();
        var json = JsonSerializer.Serialize<UiControl>(canvas, host.JsonSerializerOptions);
        json.Should().Contain("\"selected\"");
        JsonSerializer.Deserialize<UiControl>(json, host.JsonSerializerOptions)
            .Should().BeOfType<ImageCanvasControl>().Which
            .Selected.Should().BeOfType<JsonPointerReference>().Which.Pointer.Should().Be("selected");
    }

    [Fact]
    public void HitTest_PicksTheTopmostShape_SegmentsByDistance_BoxesByInside()
    {
        var shapes = new[]
        {
            new ImageCanvasShape(ImageCanvasTool.Rectangle, 10, 10, 110, 60),
            new ImageCanvasShape(ImageCanvasTool.Arrow, 0, 0, 100, 100),
        };

        ImageCanvasShapes.HitTest(shapes, 50, 50, 2).Should().Be(1, "the arrow passes through (50,50) and is drawn last, so it is on top");
        ImageCanvasShapes.HitTest(shapes, 100, 20, 2).Should().Be(0, "inside the rectangle, far from the arrow");
        ImageCanvasShapes.HitTest(shapes, 60, 90, 2).Should().Be(-1, "below the rectangle and 21 units from the arrow");
        ImageCanvasShapes.HitTest(shapes, 60, 90, 25).Should().Be(1, "within a generous tolerance of the arrow");
    }

    [Fact]
    public void TextShape_IsAPointShape_WhoseBoxGrowsWithItsText()
    {
        var text = new ImageCanvasShape(ImageCanvasTool.Text, 100, 200) { Text = "Hello", FontSize = 20 };

        text.IsPoint.Should().BeTrue();
        text.Bounds().Should().Be((100d, 180d, 160d, 205d), "5 chars × 20 × 0.6 wide; one size above the baseline, a quarter below");
        text.Distance(130, 190).Should().Be(0);
        new ImageCanvasShape(ImageCanvasTool.Text, 0, 50).Bounds().Top.Should().Be(50 - ImageCanvasShapes.DefaultFontSize);
    }

    [Fact]
    public void Move_TranslatesAndRounds_ButKeepsTheShapeOnThePicture()
    {
        var box = new ImageCanvasShape(ImageCanvasTool.Rectangle, 10, 10, 110, 60);

        box.Move(5.4, -2.6, 400, 300).Should().Be(box with { X = 15, Y = 7, X2 = 115, Y2 = 57 });
        box.Move(-50, 0, 400, 300).Should().Be(box with { X = 0, X2 = 100 }, "it stops at the left edge, keeping its size");
        box.Move(1000, 1000, 400, 300).Should().Be(box with { X = 300, Y = 250, X2 = 400, Y2 = 300 });

        var text = new ImageCanvasShape(ImageCanvasTool.Text, 390, 10) { Text = "Long label" };
        text.Move(50, -50, 400, 300).Should().Be(text with { X = 400, Y = 0 }, "a point shape keeps only its anchor on the picture");
    }

    [Fact]
    public void Draw_ClampsAndRounds_TextAnchorsAtThePress_SelectDrawsNothing()
    {
        ImageCanvasShapes.Draw(ImageCanvasTool.Arrow, -5, 10.4, 450.2, 99.6, 400, 300)
            .Should().Be(new ImageCanvasShape(ImageCanvasTool.Arrow, 0, 10, 400, 100));
        ImageCanvasShapes.Draw(ImageCanvasTool.Text, 20.7, 30, 99, 99, 400, 300)
            .Should().Be(new ImageCanvasShape(ImageCanvasTool.Text, 21, 30));
        ImageCanvasShapes.Draw(ImageCanvasTool.Select, 0, 0, 10, 10, 400, 300).Should().BeNull();
        ImageCanvasShapes.Draw("Freehand", 0, 0, 10, 10, 400, 300).Should().BeNull("an unknown tool draws nothing rather than guessing");
        ImageCanvasShapes.Draw(ImageCanvasTool.Rectangle, 10.2, 10.4, 9.8, 10.1, 400, 300)
            .Should().BeNull("both points snap to (10,10): a zero-size box would be an invisible shape");
        ImageCanvasShapes.Draw(ImageCanvasTool.Line, 10, 10, 10, 40, 400, 300)
            .Should().NotBeNull("a vertical line has zero width but is not degenerate");
    }

    [Fact]
    public void Snap_RoundsHalvesUp_LikeJavaScript()
    {
        ImageCanvasShapes.Snap(10.5).Should().Be(11, ".NET's default would give 10 (to even)");
        ImageCanvasShapes.Snap(11.5).Should().Be(12);
        ImageCanvasShapes.Snap(-0.5).Should().Be(0, "JS Math.round(-0.5) is -0");
        ImageCanvasShapes.Snap(-1.5).Should().Be(-1, "JS Math.round(-1.5) is -1");
        new ImageCanvasShape(ImageCanvasTool.Rectangle, 10, 10, 20, 20).Move(0.5, 0.5, 400, 300)
            .Should().Be(new ImageCanvasShape(ImageCanvasTool.Rectangle, 11, 11, 21, 21));
    }
}
