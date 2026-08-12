namespace MeshWeaver.Layout
{
    /// <summary>
    /// Represents an HTML control with customizable properties.
    /// </summary>
    /// <remarks>
    /// <para>Unlike every other control, this one has no element of its own: <c>Data</c> is emitted as a raw
    /// fragment, so whatever elements it contains become the direct children of the surrounding container.
    /// That is deliberate — an inline <c>&lt;span&gt;</c> stays inline, several top-level siblings stay
    /// several children of the parent stack, and a root carrying <c>flex: 1</c> is genuinely the flex child
    /// that rule applies to.</para>
    /// <para>Setting <see cref="UiControl.Style"/> or <see cref="UiControl.Class"/> opts out of that: the
    /// fragment is then wrapped in a <c>&lt;div&gt;</c> carrying them, because there is otherwise no element
    /// to put them on. Set them when the content needs a box of its own (e.g.
    /// <c>Controls.Html(svg).WithStyle("width: 100%")</c> to stop a raw <c>&lt;svg&gt;</c> collapsing to its
    /// intrinsic size as a flex child); leave them unset to keep the bare fragment.</para>
    /// <para>For more information, visit the
    /// <a href="https://www.fluentui-blazor.net/html">Fluent UI Blazor HTML documentation</a>.</para>
    /// </remarks>
    /// <param name="Data">The data associated with the HTML control.</param>
    public record HtmlControl(object Data)
        : UiControl<HtmlControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion);
}
