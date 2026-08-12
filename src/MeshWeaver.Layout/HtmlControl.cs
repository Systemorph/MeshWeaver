namespace MeshWeaver.Layout
{
    /// <summary>
    /// Represents an HTML control with customizable properties.
    /// </summary>
    /// <remarks>
    /// <para><b>By default this control contributes no element of its own</b> — <c>Data</c> is emitted as a
    /// raw fragment, so whatever elements it contains become the direct children of the surrounding
    /// container. That is deliberate: an inline <c>&lt;span&gt;</c> stays inline, several top-level siblings
    /// stay several children of the parent stack, and a root carrying <c>flex: 1</c> is genuinely the flex
    /// child that rule applies to.</para>
    /// <para><b>It is a default, not an invariant — two things opt into a wrapping <c>&lt;div&gt;</c>:</b></para>
    /// <list type="bullet">
    /// <item><description>Setting <see cref="UiControl.Style"/> or <see cref="UiControl.Class"/>, because
    /// there is otherwise no element to put them on. Set them when the content needs a box of its own (e.g.
    /// <c>Controls.Html(svg).WithStyle("width: 100%")</c> to stop a raw <c>&lt;svg&gt;</c> collapsing to its
    /// intrinsic size as a flex child); leave them unset to keep the bare fragment.</description></item>
    /// <item><description>Registering a click handler (<c>WithClickAction</c>), which needs an element to
    /// attach to. That wrapper has always been there and carries Style/Class too.</description></item>
    /// </list>
    /// <para>So the bare fragment is what you get when the control is non-clickable AND has no Style or
    /// Class; anything else introduces exactly one <c>&lt;div&gt;</c> around the fragment.</para>
    /// <para>For more information, visit the
    /// <a href="https://www.fluentui-blazor.net/html">Fluent UI Blazor HTML documentation</a>.</para>
    /// </remarks>
    /// <param name="Data">The data associated with the HTML control.</param>
    public record HtmlControl(object Data)
        : UiControl<HtmlControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion);
}
