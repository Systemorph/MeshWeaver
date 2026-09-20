namespace MeshWeaver.Layout;

/// <summary>
/// A hand-drawn signature input — a canvas the viewer signs on with a finger, a pen or the mouse,
/// the way a paper contract is signed. The bound value is the drawn signature as a PNG data URL
/// (<c>data:image/png;base64,…</c>), or the empty string while nothing has been drawn. Everything
/// else about it is the <see cref="FormControlBase{TControl}"/> surface: <c>WithLabel</c>,
/// <c>WithPlaceholder</c> (the hint under the signature line), <c>WithDisabled</c>,
/// <c>WithRequired</c>, <c>WithAriaLabel</c>.
///
/// <para>The renderer draws a signature line under the strokes, offers <b>Clear</b> and
/// <b>Done</b>, and pushes the PNG into the bound stream on <b>Done</b> — never per stroke, so a
/// signature in progress does not travel over the wire. A non-empty bound value renders as the
/// stored image, so a signature that was already given shows where it was given.</para>
/// </summary>
/// <param name="Data">The bound value — a PNG data URL, or a data-binding expression resolving to one.</param>
public record SignaturePadControl(object Data) : FormControlBase<SignaturePadControl>(Data)
{
    /// <summary>The drawing width the renderer applies when <see cref="FormControlBase{TControl}.Width"/> is unset, in CSS pixels.</summary>
    public const int DefaultWidth = 480;

    /// <summary>The drawing height the renderer applies when <see cref="FormControlBase{TControl}.Height"/> is unset, in CSS pixels.</summary>
    public const int DefaultHeight = 160;

    /// <summary>The default pen colour — a fountain-pen blue-black.</summary>
    public const string DefaultPenColor = "#1a237e";

    /// <summary>The pen colour as a CSS colour. Data-bindable.</summary>
    public object? PenColor { get; init; } = DefaultPenColor;

    /// <summary>Whether the <b>Clear</b> button is offered. Data-bindable; <c>true</c> by default.</summary>
    public object? ClearButton { get; init; } = true;

    /// <summary>Sets the pen colour (any CSS colour).</summary>
    /// <param name="penColor">The colour, or a binding expression resolving to one.</param>
    /// <returns>A new <see cref="SignaturePadControl"/> with the pen colour set.</returns>
    public SignaturePadControl WithPenColor(object penColor) => this with { PenColor = penColor };

    /// <summary>Offers or hides the <b>Clear</b> button.</summary>
    /// <param name="clearButton">Whether the button is offered.</param>
    /// <returns>A new <see cref="SignaturePadControl"/> with the setting applied.</returns>
    public SignaturePadControl WithClearButton(bool clearButton = true) => this with { ClearButton = clearButton };
}
