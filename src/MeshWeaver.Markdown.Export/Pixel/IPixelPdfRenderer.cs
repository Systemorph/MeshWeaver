namespace MeshWeaver.Markdown.Export.Pixel;

/// <summary>
/// Prints a self-contained HTML document to PDF with a real browser engine — the pixel-faithful
/// half of the export (see <see cref="Configuration.ExportFidelity"/>). It is the ONLY place a
/// browser is involved; everything upstream of it (slide selection, markdown rendering, stage CSS)
/// is ordinary reactive C# and is fully testable without one.
///
/// <para>Both members are <see cref="IObservable{T}"/> and both are cold — <b>Subscribe runs the
/// work</b>. The implementation is a <c>Process</c> leaf and therefore goes through
/// <c>IIoPool</c>'s <c>Process</c> pool, never a bare <c>Observable.FromAsync</c>.</para>
/// </summary>
public interface IPixelPdfRenderer
{
    /// <summary>
    /// Resolves the browser executable this deployment will use, or <c>null</c> when pixel-faithful
    /// rendering is not available here. Promise-cached per mesh: the file-system probe runs once and
    /// every later subscriber replays the answer.
    ///
    /// <para>Callers use this to <i>offer</i> the option — the export dialog only shows the
    /// pixel-fidelity choice when this emits a path — so "browser missing" is a capability that is
    /// absent, not an error a user runs into.</para>
    /// </summary>
    IObservable<string?> Probe();

    /// <summary>
    /// Prints <paramref name="html"/> — a complete, self-contained document; no external fetches —
    /// to PDF bytes. Errors (browser missing, non-zero exit, timeout) surface as <c>OnError</c>;
    /// nothing is swallowed.
    /// </summary>
    IObservable<byte[]> Render(string html);
}
