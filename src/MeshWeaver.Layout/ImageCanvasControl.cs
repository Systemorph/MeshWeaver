namespace MeshWeaver.Layout;

/// <summary>
/// A picture the viewer edits by dragging on it — the platform's control for DIRECT MANIPULATION
/// of an image. Today it carries one tool: a crop region with handles on its edges and corners.
///
/// <para><b>A thin overlay, never a second image renderer.</b> The renderer draws
/// <see cref="Image"/> exactly as given (the server has already rendered it) and, on top, only
/// interaction chrome — the crop frame, the dimmed outside, the handles. A drag is shown locally
/// while it lasts and commits ONCE, on release, as ordinary field edits of the four bound trims
/// (<see cref="CropLeft"/>, <see cref="CropTop"/>, <see cref="CropRight"/>,
/// <see cref="CropBottom"/>). The server re-renders and the new picture flows back. Keeping all
/// image processing on the server is what lets every renderer (Blazor, the JS shells) stay a few
/// lines of pointer handling — <c>Doc/GUI/CrossRendererAuthoring</c>, rule 2.</para>
///
/// <para><b>Model coordinates.</b> The bound values are in MODEL units, not screen pixels: the
/// picture's full extent is <see cref="ModelWidth"/> × <see cref="ModelHeight"/> (typically the
/// source image's pixels), whatever size it is drawn at. A renderer converts a drag of
/// <c>d</c> screen pixels into <c>d · ModelWidth / drawnWidth</c> model units and rounds to an
/// integer. Each trim is the amount removed from that edge; absent means 0. The geometry rules
/// a renderer must apply are <see cref="ImageCanvasCrop.Normalize"/>.</para>
/// </summary>
/// <param name="Image">The picture as an image URL or <c>data:image/…</c> URI, or a binding to one.</param>
/// <param name="Alt">The accessible name of the picture, localized by the caller. A literal or a binding.</param>
public record ImageCanvasControl(object Image, object Alt)
    : UiControl<ImageCanvasControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>The tallest the picture is drawn, as a CSS length, unless authored otherwise.</summary>
    public const string DefaultMaxHeight = "70vh";

    /// <summary>The picture's full width in model units (e.g. source pixels). Required for the crop tool. A literal or a binding.</summary>
    public object? ModelWidth { get; init; }

    /// <summary>The picture's full height in model units. Required for the crop tool. A literal or a binding.</summary>
    public object? ModelHeight { get; init; }

    /// <summary>Model units removed from the left edge — typically a binding the renderer writes back. Absent = 0.</summary>
    public object? CropLeft { get; init; }

    /// <summary>Model units removed from the top edge — typically a binding. Absent = 0.</summary>
    public object? CropTop { get; init; }

    /// <summary>Model units removed from the right edge — typically a binding. Absent = 0.</summary>
    public object? CropRight { get; init; }

    /// <summary>Model units removed from the bottom edge — typically a binding. Absent = 0.</summary>
    public object? CropBottom { get; init; }

    /// <summary>The tallest the picture is drawn, as a CSS length; <see cref="DefaultMaxHeight"/> when unset. Its width never exceeds the container.</summary>
    public object? MaxHeight { get; init; }

    /// <summary>Whether a crop is bound at all — the renderer shows the crop tool only then.</summary>
    public bool HasCrop => CropLeft is not null || CropTop is not null || CropRight is not null || CropBottom is not null;

    /// <summary>Sets the picture's full extent in model units.</summary>
    /// <param name="width">The width, or a binding.</param>
    /// <param name="height">The height, or a binding.</param>
    /// <returns>A new <see cref="ImageCanvasControl"/> with the model size set.</returns>
    public ImageCanvasControl WithModelSize(object width, object height) => this with { ModelWidth = width, ModelHeight = height };

    /// <summary>Binds the crop tool to the four trims (model units removed per edge).</summary>
    /// <param name="left">The left trim, typically a binding.</param>
    /// <param name="top">The top trim, typically a binding.</param>
    /// <param name="right">The right trim, typically a binding.</param>
    /// <param name="bottom">The bottom trim, typically a binding.</param>
    /// <returns>A new <see cref="ImageCanvasControl"/> with the crop tool bound.</returns>
    public ImageCanvasControl WithCrop(object left, object top, object right, object bottom)
        => this with { CropLeft = left, CropTop = top, CropRight = right, CropBottom = bottom };

    /// <summary>Sets the tallest the picture is drawn.</summary>
    /// <param name="maxHeight">A CSS length, or a binding.</param>
    /// <returns>A new <see cref="ImageCanvasControl"/> with the maximum height set.</returns>
    public ImageCanvasControl WithMaxHeight(object maxHeight) => this with { MaxHeight = maxHeight };

    /// <summary>Sets the accessible name.</summary>
    /// <param name="alt">The localized name, or a binding.</param>
    /// <returns>A new <see cref="ImageCanvasControl"/> with the name set.</returns>
    public ImageCanvasControl WithAlt(object alt) => this with { Alt = alt };
}

/// <summary>
/// A crop in model units: the amount removed from each edge of a picture that is
/// <see cref="Width"/> × <see cref="Height"/> model units large.
/// </summary>
/// <param name="Left">Units removed from the left edge.</param>
/// <param name="Top">Units removed from the top edge.</param>
/// <param name="Right">Units removed from the right edge.</param>
/// <param name="Bottom">Units removed from the bottom edge.</param>
/// <param name="Width">The picture's full width in model units.</param>
/// <param name="Height">The picture's full height in model units.</param>
public record ImageCanvasCrop(int Left, int Top, int Right, int Bottom, int Width, int Height)
{
    /// <summary>
    /// The ONE geometry rule every renderer applies before writing a crop back, so the four
    /// trims always describe a real region: each trim is clamped to <c>[0, extent − 1]</c>, and
    /// when the two trims of an axis would leave less than one unit, the trim on a
    /// <paramref name="moving"/> edge gives way (the edge the viewer is dragging stops at the other
    /// one instead of pushing it). A corner drag moves two edges at once (e.g. Top and Left). With
    /// no moving edge (normalising stored values) the right and bottom trims give way.
    /// </summary>
    /// <param name="moving">The edges being dragged — values of <see cref="ImageCanvasEdge"/>; unknown values are ignored.</param>
    /// <returns>The normalised crop.</returns>
    public ImageCanvasCrop Normalize(params string[] moving)
    {
        var w = Math.Max(1, Width);
        var h = Math.Max(1, Height);
        var (left, right) = Axis(Left, Right, w, moving.Contains(ImageCanvasEdge.Left));
        var (top, bottom) = Axis(Top, Bottom, h, moving.Contains(ImageCanvasEdge.Top));
        return this with { Left = left, Top = top, Right = right, Bottom = bottom, Width = w, Height = h };
    }

    /// <summary>One axis: clamp both trims, then make the moving one give way so one unit remains.</summary>
    private static (int Start, int End) Axis(int start, int end, int extent, bool startIsMoving)
    {
        start = Math.Clamp(start, 0, extent - 1);
        end = Math.Clamp(end, 0, extent - 1);
        var overflow = start + end - (extent - 1);
        if (overflow > 0)
        {
            if (startIsMoving) start -= overflow;
            else end -= overflow;
        }
        return (start, end);
    }
}

/// <summary>
/// The edges of an <see cref="ImageCanvasCrop"/> — an OPEN vocabulary of string constants
/// (policy <c>open-vocabulary-string-constants</c>). An unknown value is treated as "no edge".
/// </summary>
public static class ImageCanvasEdge
{
    /// <summary>The left edge.</summary>
    public const string Left = "Left";
    /// <summary>The top edge.</summary>
    public const string Top = "Top";
    /// <summary>The right edge.</summary>
    public const string Right = "Right";
    /// <summary>The bottom edge.</summary>
    public const string Bottom = "Bottom";
}
