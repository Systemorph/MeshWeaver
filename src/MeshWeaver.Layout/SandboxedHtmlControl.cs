namespace MeshWeaver.Layout;

/// <summary>
/// The rules every <see cref="SandboxedHtmlControl"/> renderer applies, in one place so Blazor and React
/// frame the same way. A renderer writes the <see cref="SandboxedHtmlControl.Document"/> as the frame's
/// <c>srcdoc</c> and these attributes — and NOTHING it takes from the control.
/// </summary>
public static class SandboxedHtmlFrame
{
    /// <summary>
    /// The frame's <c>sandbox</c>, fixed: no <c>allow-scripts</c> (nothing in the document runs), no
    /// <c>allow-same-origin</c> (the document is an opaque origin — no cookies, no storage, no access to the
    /// portal), no forms, no top navigation. A link may open a NEW tab (<c>allow-popups</c>), and that tab is a
    /// normal page (<c>allow-popups-to-escape-sandbox</c>) — the frame itself can never navigate the portal.
    /// </summary>
    public const string Sandbox = "allow-popups allow-popups-to-escape-sandbox";

    /// <summary>The frame's <c>referrerpolicy</c>: the portal's address never leaves with a request the document makes.</summary>
    public const string ReferrerPolicy = "no-referrer";

    /// <summary>The default frame height.</summary>
    public const string DefaultHeight = "70vh";
}

/// <summary>
/// A complete HTML document shown in a SANDBOXED frame — a mail's own layout in the inbox's reading pane —
/// instead of a hand-built <c>Controls.Html("&lt;iframe srcdoc=…&gt;")</c>. Renderers apply
/// <see cref="SandboxedHtmlFrame"/>: scripts never run, the document has no origin, and links open in a new tab.
///
/// <para>🚨 The control does NOT sanitise. <see cref="Document"/> must already be safe — produced by a
/// sanitiser on the server (the inbox's is <c>MeshWeaver.Graph.MailHtml</c>: an allow-list of tags, attributes,
/// URL schemes and CSS, remote images blocked unless the viewer asked for them, and a Content-Security-Policy
/// in the document's own head). The sandbox is the SECOND line, never the only one.</para>
/// </summary>
/// <param name="Document">The complete, already sanitised document — written as the frame's <c>srcdoc</c>. A literal or a binding.</param>
/// <param name="Title">The frame's accessible name (its <c>title</c>). User-facing: pass a localized string.</param>
public record SandboxedHtmlControl(object Document, object Title)
    : UiControl<SandboxedHtmlControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>The frame height as a CSS length; <see cref="SandboxedHtmlFrame.DefaultHeight"/> by default. A literal or a binding.</summary>
    public object? Height { get; init; } = SandboxedHtmlFrame.DefaultHeight;

    /// <summary>Sets the frame height (<see cref="Height"/>).</summary>
    /// <param name="height">A CSS length, or a binding.</param>
    /// <returns>A new <see cref="SandboxedHtmlControl"/> with the height set.</returns>
    public SandboxedHtmlControl WithHeight(object height) => this with { Height = height };
}
