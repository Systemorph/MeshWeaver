namespace MeshWeaver.Layout;

/// <summary>
/// Static, dependency-free classification of the PLACEHOLDER FRAMES a layout area can serve.
/// The twin of <see cref="AreaErrorClassifier"/>: that one classifies the exceptions an area
/// subscription surfaces, this one classifies the controls it RENDERS when the area's real
/// content is not (yet) available.
///
/// <para>Why frames need classifying at all: the two placeholders look alike to a human and
/// mean opposite things to a consumer. <b>Area not found</b> is a VERDICT — nothing on this hub
/// will ever render this area, so a waiter should give up and say so. The <b>compile-progress</b>
/// page is a PROMISE — the instance's NodeType is still building, and the real content arrives
/// (via a <see cref="RedirectControl"/> the moment the build settles) without anyone doing
/// anything. A consumer that cannot tell them apart either gives up on a page that was merely
/// compiling, or waits forever on an area that genuinely does not exist.</para>
///
/// <para>The distinction is carried by <see cref="UiControl.Id"/> — a stable, well-known token,
/// never the prose. The prose is localized, reworded, and read by humans; the id round-trips
/// through the sync stream as a plain JSON string, so a client that only ever sees serialized
/// frames (the plugin-gate probe) can match the same constant the typed predicates below use.</para>
/// </summary>
public static class AreaFrameClassifier
{
    /// <summary>
    /// <see cref="UiControl.Id"/> of the framework's "Area not found" placeholder
    /// (<c>LayoutDefinition.BuildNotFoundControl</c>) — no renderer on this hub produced the
    /// requested area. TERMINAL.
    /// </summary>
    public const string AreaNotFoundId = "area-not-found";

    /// <summary>
    /// <see cref="UiControl.Id"/> of every frame the compile-progress surface serves
    /// (<c>NodeTypeLayoutAreas.CompileProgressView</c>) — the instance's NodeType has not
    /// finished building, so this area is not being served YET. TRANSIENT.
    /// </summary>
    public const string CompileProgressId = "compile-progress";

    /// <summary>
    /// <see cref="UiControl.Id"/> of the frame an area serves when its content REFERENCES a node
    /// that does not exist (<c>LayoutAreaHost.RenderRenderingError</c> on a routing
    /// <c>ErrorType.NotFound</c>) — a deck manifest naming a slide that was never created,
    /// an embed left pointing at a deleted node, a copied page whose relative reference does not
    /// resolve in its new location. TERMINAL.
    ///
    /// <para>The THIRD state, and it is genuinely distinct from the other two (#1456). Against
    /// <see cref="AreaNotFoundId"/>: the area itself is registered and rendering fine — it is the
    /// DATA it points at that is absent, so "no renderer for this area" would send an author
    /// looking in exactly the wrong place. Against <see cref="CompileProgressId"/>: nothing is
    /// going to arrive, so a consumer that waits for this one waits forever.</para>
    /// </summary>
    public const string MissingReferenceId = "reference-missing";

    // The pre-id signal, kept as a fallback so a frame that lost its id on the way here (an
    // older peer, a control rebuilt from partial JSON) is still recognised. Never localize:
    // BuildNotFoundControl is deliberately English — it is a framework diagnostic, not UI copy.
    private const string AreaNotFoundMarkdownMarker = "**Area not found**";

    /// <summary>
    /// True for the framework's "Area not found" placeholder: NO renderer is registered for the
    /// requested area on the hub that answered. Distinct from <see cref="IsCompileProgress"/> —
    /// see the type remarks for why conflating them is the bug this class exists to prevent.
    /// </summary>
    /// <param name="control">The rendered control, or <c>null</c>.</param>
    public static bool IsAreaNotFound(UiControl? control)
        => HasFrameId(control, AreaNotFoundId)
           || (control is MarkdownControl markdown
               && (markdown.Markdown?.ToString() ?? string.Empty)
                   .Contains(AreaNotFoundMarkdownMarker, StringComparison.Ordinal));

    /// <summary>
    /// True for a frame served by the compile-progress surface — the live status page an
    /// instance serves on EVERY area while its NodeType is still building.
    /// </summary>
    /// <param name="control">The rendered control, or <c>null</c>.</param>
    public static bool IsCompileProgress(UiControl? control)
        => HasFrameId(control, CompileProgressId);

    /// <summary>
    /// True for the frame an area serves when the content it renders REFERENCES a node that does
    /// not exist. The reference is bad DATA — an author has to fix the manifest, the embed or the
    /// link — so this is terminal, and deliberately NOT part of <see cref="IsTransientFrame"/>.
    /// </summary>
    /// <param name="control">The rendered control, or <c>null</c>.</param>
    public static bool IsMissingReference(UiControl? control)
        => HasFrameId(control, MissingReferenceId);

    /// <summary>
    /// True for a frame that is not the area's content and will be REPLACED without anyone
    /// acting: the compile-progress page, and the <see cref="RedirectControl"/> it emits once
    /// the build settles. The single predicate a waiter needs — "keep waiting, this is not the
    /// answer". A genuinely missing area (<see cref="IsAreaNotFound"/>) is deliberately NOT
    /// transient: nothing is going to replace it.
    /// </summary>
    /// <param name="control">The rendered control, or <c>null</c>.</param>
    public static bool IsTransientFrame(UiControl? control)
        => IsCompileProgress(control) || control is RedirectControl;

    // UiControl.Id is `object?`, so a frame that came back over the sync stream carries it as
    // whatever the deserializer produced for a JSON string (a JsonElement, not a string). Compare
    // the rendered text, never the CLR type — the typed check silently answered "no" for every
    // client-side frame, which is the only place these predicates matter.
    private static bool HasFrameId(UiControl? control, string id)
        => control?.Id is { } actual && string.Equals(actual.ToString(), id, StringComparison.Ordinal);
}
