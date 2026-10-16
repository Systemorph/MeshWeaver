using System.Collections.Immutable;
using Net.Codecrete.QrCodeGenerator;

namespace MeshWeaver.Layout;

/// <summary>
/// The error-correction level <see cref="QrCodes.Encode"/> encodes with — an OPEN vocabulary of
/// string constants (policy <c>open-vocabulary-string-constants</c>). A value this build does not
/// know is refused by name, never mapped to a silently weaker or stronger level.
/// </summary>
public static class QrErrorCorrection
{
    /// <summary>About 7% of the code may be damaged.</summary>
    public const string Low = "Low";

    /// <summary>About 15% — the default, suited to a code shown on a screen.</summary>
    public const string Medium = "Medium";

    /// <summary>About 25%.</summary>
    public const string Quartile = "Quartile";

    /// <summary>About 30%.</summary>
    public const string High = "High";
}

/// <summary>
/// The QR encoder of the layout layer: text in, the module matrix out — computed on the SERVER, so
/// a <see cref="QrCodeControl"/> carries the finished code and every renderer only paints squares.
/// No renderer needs a QR library, and no third-party service ever sees the encoded text.
/// </summary>
public static class QrCodes
{
    /// <summary>The quiet zone the standard requires around a code, in modules. Renderers add it; the matrix does not contain it.</summary>
    public const int QuietZone = 4;

    /// <summary>
    /// The module matrix of <paramref name="text"/>: one string per row, top to bottom, each of
    /// <c>'1'</c> (dark) and <c>'0'</c> (light) left to right — a square of the code's own size
    /// (21 to 177), WITHOUT the quiet zone. An empty <paramref name="text"/> is the empty matrix
    /// (nothing to draw). Pure.
    /// </summary>
    /// <param name="text">The text to encode.</param>
    /// <param name="errorCorrection">One of <see cref="QrErrorCorrection"/>.</param>
    /// <exception cref="ArgumentException">The level is not one this build knows.</exception>
    public static ImmutableList<string> Encode(string? text, string errorCorrection = QrErrorCorrection.Medium)
    {
        var ecc = errorCorrection switch
        {
            QrErrorCorrection.Low => QrCode.Ecc.Low,
            QrErrorCorrection.Medium => QrCode.Ecc.Medium,
            QrErrorCorrection.Quartile => QrCode.Ecc.Quartile,
            QrErrorCorrection.High => QrCode.Ecc.High,
            _ => throw new ArgumentException(
                $"'{errorCorrection}' is not a QR error-correction level this build knows "
                + $"({QrErrorCorrection.Low}, {QrErrorCorrection.Medium}, {QrErrorCorrection.Quartile}, {QrErrorCorrection.High})",
                nameof(errorCorrection)),
        };
        if (string.IsNullOrEmpty(text))
            return [];
        var code = QrCode.EncodeText(text, ecc);
        var rows = ImmutableList.CreateBuilder<string>();
        var row = new char[code.Size];
        for (var y = 0; y < code.Size; y++)
        {
            for (var x = 0; x < code.Size; x++)
                row[x] = code.GetModule(x, y) ? '1' : '0';
            rows.Add(new string(row));
        }
        return rows.ToImmutable();
    }
}

/// <summary>
/// A QR code, drawn from a module matrix the SERVER computed (<see cref="QrCodes.Encode"/>, or
/// <see cref="Controls.QrCodeOf"/>): every renderer paints the dark modules as squares, black on
/// white, with the standard four-module quiet zone around them — in the dark theme too, because a
/// camera needs the contrast — and nothing ever drawn over the code.
///
/// <para>🚨 Whatever was encoded is readable by every camera that sees the screen. Encode a public
/// address — never a credential, a personal detail or a single-use token — and show the same
/// address as text beside the code (with a <see cref="ShareLinkControl"/>), so the code is never the
/// only way to it.</para>
///
/// <para>An empty matrix renders nothing, so a bound matrix that is cleared removes the code.</para>
/// </summary>
/// <param name="Modules">The matrix — rows of <c>'1'</c>/<c>'0'</c> as <see cref="QrCodes.Encode"/> returns them — or a data-binding pointer to one.</param>
public record QrCodeControl(object Modules) : UiControl<QrCodeControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>The default rendered width, in CSS pixels — large enough to scan at a meeting-table distance.</summary>
    public const int DefaultSize = 240;

    /// <summary>The accessible name of the code (what a screen reader announces) — a literal or a binding.</summary>
    public object? Label { get; init; }

    /// <summary>The rendered width in CSS pixels, quiet zone included (the code is square); defaults to <see cref="DefaultSize"/>.</summary>
    public object Size { get; init; } = DefaultSize;

    /// <summary>Sets the accessible name.</summary>
    /// <param name="label">The label, or a binding.</param>
    public QrCodeControl WithLabel(object label) => this with { Label = label };

    /// <summary>Sets the rendered width in CSS pixels.</summary>
    /// <param name="size">The width, or a binding.</param>
    public QrCodeControl WithSize(object size) => this with { Size = size };
}
