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
/// <para><b>Shapes.</b> When <see cref="Shapes"/> is bound to a list, the viewer also draws,
/// selects, moves and deletes vector shapes (annotations) with the active <see cref="Tool"/>.
/// The renderer never DRAWS the shapes — they are part of the server-rendered picture — it only
/// hit-tests them, outlines the selected one and shows the gesture in progress. Each finished
/// gesture writes the WHOLE list back once; every element property other than the geometry
/// (<see cref="ImageCanvasShape"/>) is carried through unchanged, and a new shape copies
/// <see cref="NewShape"/>. The rules are <see cref="ImageCanvasShapes"/>.</para>
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

    /// <summary>
    /// The shapes drawn on the picture — a binding to a list whose elements carry at least the
    /// <see cref="ImageCanvasShape"/> members (<c>kind, x, y, x2, y2</c>, plus <c>text, fontSize</c>
    /// for a text shape), in model units. Absent = no shape tools.
    /// </summary>
    public object? Shapes { get; init; }

    /// <summary>The active tool — a value of <see cref="ImageCanvasTool"/> (unknown = <see cref="ImageCanvasTool.Select"/>). A literal or a binding.</summary>
    public object? Tool { get; init; }

    /// <summary>
    /// The template a new shape copies before its kind and geometry are set — e.g.
    /// <c>{ color, strokeWidth, fontSize, text }</c>. A literal object or a binding; absent = geometry only.
    /// </summary>
    public object? NewShape { get; init; }

    /// <summary>Whether shapes are bound — the renderer offers the shape tools only then.</summary>
    public bool HasShapes => Shapes is not null;

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

    /// <summary>Binds the shape tools.</summary>
    /// <param name="shapes">The list of shapes, typically a binding the renderer writes back.</param>
    /// <param name="tool">The active tool (<see cref="ImageCanvasTool"/>), a literal or a binding.</param>
    /// <param name="newShape">The template a new shape copies (its style), or null.</param>
    /// <returns>A new <see cref="ImageCanvasControl"/> with the shape tools bound.</returns>
    public ImageCanvasControl WithShapes(object shapes, object tool, object? newShape = null)
        => this with { Shapes = shapes, Tool = tool, NewShape = newShape };

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

/// <summary>
/// The tools of an <see cref="ImageCanvasControl"/> with bound <see cref="ImageCanvasControl.Shapes"/> —
/// an OPEN vocabulary of string constants. Each drawing tool's value is also the <c>kind</c> it gives
/// the shapes it draws. An unknown value behaves as <see cref="Select"/>.
/// </summary>
public static class ImageCanvasTool
{
    /// <summary>Click a shape to select it; drag it to move it; Delete removes it.</summary>
    public const string Select = "Select";
    /// <summary>Drag a box (outline).</summary>
    public const string Rectangle = "Rectangle";
    /// <summary>Drag the box an ellipse fills.</summary>
    public const string Ellipse = "Ellipse";
    /// <summary>Drag from one end to the other.</summary>
    public const string Line = "Line";
    /// <summary>Drag from the tail to the head.</summary>
    public const string Arrow = "Arrow";
    /// <summary>Drag a translucent filled box.</summary>
    public const string Highlight = "Highlight";
    /// <summary>Drag the box to blur.</summary>
    public const string Blur = "Blur";
    /// <summary>Click where the text starts (the left end of its baseline).</summary>
    public const string Text = "Text";

    /// <summary>The tools that draw with a drag from one point to another (every drawing tool but <see cref="Text"/>).</summary>
    public static readonly IReadOnlyList<string> DragTools = [Rectangle, Ellipse, Line, Arrow, Highlight, Blur];

    /// <summary>The kinds drawn as a segment from (x, y) to (x2, y2) — hit-tested by distance to that segment, not by their box.</summary>
    public static readonly IReadOnlyList<string> SegmentKinds = [Line, Arrow];
}

/// <summary>
/// The geometry of one shape, in model units — all a renderer needs to hit-test and move it. A
/// shape with both <see cref="X2"/> and <see cref="Y2"/> spans from (X, Y) to (X2, Y2); one without
/// is a POINT shape (text) anchored at the left end of its baseline.
/// </summary>
/// <param name="Kind">The shape's kind — an <see cref="ImageCanvasTool"/> drawing value, or any other (treated as a box).</param>
/// <param name="X">The first point's x.</param>
/// <param name="Y">The first point's y.</param>
/// <param name="X2">The second point's x, or null for a point shape.</param>
/// <param name="Y2">The second point's y, or null for a point shape.</param>
public record ImageCanvasShape(string Kind, double X, double Y, double? X2 = null, double? Y2 = null)
{
    /// <summary>The text of a text shape (its box grows with it).</summary>
    public string? Text { get; init; }

    /// <summary>The font size of a text shape in model units; <see cref="ImageCanvasShapes.DefaultFontSize"/> when unset.</summary>
    public double? FontSize { get; init; }

    /// <summary>Whether the shape is a single anchored point (text) rather than a span.</summary>
    public bool IsPoint => X2 is null || Y2 is null;

    /// <summary>Whether the shape is a segment (line, arrow), hit-tested by distance to it.</summary>
    public bool IsSegment => !IsPoint && ImageCanvasTool.SegmentKinds.Contains(Kind);

    /// <summary>
    /// The shape's box. A span gives the box of its two points; a point shape gives its text's box,
    /// estimated as <see cref="ImageCanvasShapes.CharWidth"/> × size per character, from one size
    /// above the baseline to a quarter below it.
    /// </summary>
    /// <returns>Left, top, right and bottom in model units.</returns>
    public (double Left, double Top, double Right, double Bottom) Bounds()
    {
        if (!IsPoint)
            return (Math.Min(X, X2!.Value), Math.Min(Y, Y2!.Value), Math.Max(X, X2.Value), Math.Max(Y, Y2.Value));
        var size = FontSize is > 0 ? FontSize.Value : ImageCanvasShapes.DefaultFontSize;
        var length = Math.Max(1, (Text ?? "").Length);
        return (X, Y - size, X + length * size * ImageCanvasShapes.CharWidth, Y + size / 4);
    }

    /// <summary>How far a point is from the shape: 0 inside its box (a segment: on it), else the distance.</summary>
    /// <param name="px">The point's x in model units.</param>
    /// <param name="py">The point's y in model units.</param>
    /// <returns>The distance in model units.</returns>
    public double Distance(double px, double py)
    {
        if (IsSegment)
        {
            double dx = X2!.Value - X, dy = Y2!.Value - Y;
            var lengthSquared = dx * dx + dy * dy;
            var t = lengthSquared == 0 ? 0 : Math.Clamp(((px - X) * dx + (py - Y) * dy) / lengthSquared, 0, 1);
            return Math.Sqrt(Math.Pow(px - (X + t * dx), 2) + Math.Pow(py - (Y + t * dy), 2));
        }
        var (left, top, right, bottom) = Bounds();
        var ox = Math.Max(Math.Max(left - px, 0), px - right);
        var oy = Math.Max(Math.Max(top - py, 0), py - bottom);
        return Math.Sqrt(ox * ox + oy * oy);
    }

    /// <summary>
    /// The shape translated by (dx, dy), snapped to whole units (<see cref="ImageCanvasShapes.Snap"/>), with the translation clamped so
    /// its points stay on the picture (<c>[0, width] × [0, height]</c>). A point shape keeps only its
    /// anchor on the picture.
    /// </summary>
    /// <param name="dx">The wanted x translation in model units.</param>
    /// <param name="dy">The wanted y translation in model units.</param>
    /// <param name="width">The picture's width in model units.</param>
    /// <param name="height">The picture's height in model units.</param>
    /// <returns>The moved shape.</returns>
    public ImageCanvasShape Move(double dx, double dy, double width, double height)
    {
        var (minX, maxX) = IsPoint ? (X, X) : (Math.Min(X, X2!.Value), Math.Max(X, X2.Value));
        var (minY, maxY) = IsPoint ? (Y, Y) : (Math.Min(Y, Y2!.Value), Math.Max(Y, Y2.Value));
        dx = ImageCanvasShapes.Snap(Math.Clamp(dx, -minX, Math.Max(-minX, width - maxX)));
        dy = ImageCanvasShapes.Snap(Math.Clamp(dy, -minY, Math.Max(-minY, height - maxY)));
        return this with { X = X + dx, Y = Y + dy, X2 = X2 + dx, Y2 = Y2 + dy };
    }
}

/// <summary>
/// The shape rules every renderer of <see cref="ImageCanvasControl.Shapes"/> applies — one
/// definition, mirrored by the JS renderers, so a gesture means the same thing everywhere.
/// </summary>
public static class ImageCanvasShapes
{
    /// <summary>Screen pixels a pointer must travel before a press becomes a drag (below it, it is a click).</summary>
    public const double DragThreshold = 4;

    /// <summary>Screen pixels within which a click still hits a shape — divide by the drawn scale to get model units.</summary>
    public const double HitTolerance = 6;

    /// <summary>The font size assumed for a text shape that states none, in model units.</summary>
    public const double DefaultFontSize = 32;

    /// <summary>The estimated width of one character as a fraction of the font size.</summary>
    public const double CharWidth = 0.6;

    /// <summary>
    /// Snaps a model coordinate to a whole unit, halves UP (<c>⌊v + ½⌋</c>) — exactly JavaScript's
    /// <c>Math.round</c>, so the JS renderers and this rule agree on every value, midpoints included
    /// (.NET's default <c>Math.Round</c> rounds halves to even).
    /// </summary>
    /// <param name="value">The coordinate in model units.</param>
    /// <returns>The snapped coordinate.</returns>
    public static double Snap(double value) => Math.Floor(value + 0.5);

    /// <summary>The topmost shape within <paramref name="tolerance"/> of a point — the LAST in the list, as it is drawn last.</summary>
    /// <param name="shapes">The shapes in drawing order.</param>
    /// <param name="x">The point's x in model units.</param>
    /// <param name="y">The point's y in model units.</param>
    /// <param name="tolerance">The hit tolerance in model units.</param>
    /// <returns>The index of the hit shape, or -1.</returns>
    public static int HitTest(IReadOnlyList<ImageCanvasShape> shapes, double x, double y, double tolerance)
    {
        for (var i = shapes.Count - 1; i >= 0; i--)
            if (shapes[i].Distance(x, y) <= tolerance)
                return i;
        return -1;
    }

    /// <summary>
    /// The shape a drawing tool makes from a gesture: a drag tool spans from the press to the
    /// release, a text tool is anchored at the press. Points are clamped to the picture and snapped
    /// (<see cref="Snap"/>); a drag whose points snap to the same one draws nothing.
    /// </summary>
    /// <param name="tool">The active tool.</param>
    /// <param name="x0">The press x in model units.</param>
    /// <param name="y0">The press y in model units.</param>
    /// <param name="x1">The release x in model units.</param>
    /// <param name="y1">The release y in model units.</param>
    /// <param name="width">The picture's width in model units.</param>
    /// <param name="height">The picture's height in model units.</param>
    /// <returns>The new shape's geometry, or null when the tool draws nothing (Select, unknown, or a degenerate drag).</returns>
    public static ImageCanvasShape? Draw(string? tool, double x0, double y0, double x1, double y1, double width, double height)
    {
        double Cx(double v) => Snap(Math.Clamp(v, 0, width));
        double Cy(double v) => Snap(Math.Clamp(v, 0, height));
        if (tool == ImageCanvasTool.Text)
            return new ImageCanvasShape(tool, Cx(x0), Cy(y0));
        if (tool is null || !ImageCanvasTool.DragTools.Contains(tool))
            return null;
        var shape = new ImageCanvasShape(tool, Cx(x0), Cy(y0), Cx(x1), Cy(y1));
        // A drag whose two points snap to the same one draws nothing: a zero-size box or zero-length
        // segment would be an invisible shape that still hit-tests and fills the list.
        return shape.X == shape.X2 && shape.Y == shape.Y2 ? null : shape;
    }
}
