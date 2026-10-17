using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout;

/// <summary>
/// The intent of a <see cref="MessageBarControl"/> — an OPEN vocabulary of string constants (policy
/// <c>open-vocabulary-string-constants</c>). A renderer that meets a value it does not know draws the
/// neutral <see cref="Info"/> styling and names the value it did not recognise; it never maps it onto
/// <see cref="Warning"/> or <see cref="Error"/>, so an unknown value is never louder than it said.
/// </summary>
public static class MessageBarIntent
{
    /// <summary>Neutral information — the default. Announced politely (<c>role="status"</c>).</summary>
    public const string Info = "Info";

    /// <summary>Something completed as asked. Announced politely (<c>role="status"</c>).</summary>
    public const string Success = "Success";

    /// <summary>Something the reader should look at before going on. Announced assertively (<c>role="alert"</c>).</summary>
    public const string Warning = "Warning";

    /// <summary>Something failed. Announced assertively (<c>role="alert"</c>).</summary>
    public const string Error = "Error";

    /// <summary>
    /// The ARIA role a renderer gives a bar of <paramref name="intent"/>: <c>alert</c> for
    /// <see cref="Warning"/> and <see cref="Error"/> (an assertive live region — a screen reader
    /// interrupts to read it), <c>status</c> for everything else, an unknown or absent intent included
    /// (a polite live region). Pure; matched ignoring case, as the renderers match the intent itself.
    /// </summary>
    /// <param name="intent">The bar's intent, or <c>null</c>.</param>
    /// <returns><c>"alert"</c> or <c>"status"</c>.</returns>
    public static string AriaRoleOf(string? intent) =>
        string.Equals(intent, Warning, StringComparison.OrdinalIgnoreCase)
        || string.Equals(intent, Error, StringComparison.OrdinalIgnoreCase)
            ? "alert"
            : "status";
}

/// <summary>
/// An info / success / warning / error banner: an optional bold <see cref="Title"/>, the
/// <see cref="Body"/> text, and an optional action beside it (<see cref="WithAction"/>). Use it for a
/// message ABOUT the page — "read-only", "not connected", "saved", "the sync failed" — instead of a
/// hand-built <c>Controls.Html("&lt;div style=…&gt;")</c> box. Renderers give it the role its
/// <see cref="Intent"/> calls for (<see cref="MessageBarIntent.AriaRoleOf"/>) and the platform's own
/// intent colours and icon in both themes.
///
/// <para>Every value is a literal or a data-binding pointer, so a bound bar changes intent, title and
/// text in place. The body is plain text; for a body with links or emphasis, compose a
/// <see cref="Controls.Markdown"/> or a <see cref="LinkControl"/> as the action instead of putting
/// markup in it. The text is user-facing — pass localized strings.</para>
/// </summary>
/// <param name="Body">The message text — a literal or a data-binding pointer.</param>
public record MessageBarControl(object Body)
    : UiControl<MessageBarControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>The name of the child area the action renders into, under the bar's own area (<c>{area}/Action</c>).</summary>
    public const string ActionArea = "Action";

    /// <summary>One of <see cref="MessageBarIntent"/> (or a binding to one); <see cref="MessageBarIntent.Info"/> when unset.</summary>
    public object? Intent { get; init; } = MessageBarIntent.Info;

    /// <summary>The bold lead-in shown before the body — a literal or a binding; optional.</summary>
    public object? Title { get; init; }

    /// <summary>
    /// The control shown as the bar's action — typically a <see cref="ButtonControl"/> (with its own
    /// click action or <see cref="ButtonControl.NavigateToHref"/>) or a <see cref="LinkControl"/>.
    /// It is rendered as a child area (<see cref="ActionArea"/>), so its click reaches its own handler
    /// exactly as it would anywhere else on the page. Not serialized; renderers read
    /// <see cref="HasAction"/>.
    /// </summary>
    internal UiControl? Action { get; init; }

    /// <summary>True when <see cref="WithAction"/> set an action, so renderers draw the <see cref="ActionArea"/> child area.</summary>
    public bool HasAction { get; init; }

    /// <summary>Sets the intent.</summary>
    /// <param name="intent">One of <see cref="MessageBarIntent"/>, or a binding to one.</param>
    /// <returns>A new <see cref="MessageBarControl"/> with the intent set.</returns>
    public MessageBarControl WithIntent(object intent) => this with { Intent = intent };

    /// <summary>Sets the bold lead-in.</summary>
    /// <param name="title">The title, or a binding.</param>
    /// <returns>A new <see cref="MessageBarControl"/> with the title set.</returns>
    public MessageBarControl WithTitle(object title) => this with { Title = title };

    /// <summary>Sets the action shown beside the body (<see cref="Action"/>).</summary>
    /// <param name="action">The action control — a <see cref="ButtonControl"/> or a <see cref="LinkControl"/>.</param>
    /// <returns>A new <see cref="MessageBarControl"/> with the action set.</returns>
    public MessageBarControl WithAction(UiControl action) => this with { Action = action, HasAction = true };

    /// <summary>
    /// Renders the bar, then its action (when set) into the <see cref="ActionArea"/> child area.
    /// Called by the framework during layout-area rendering; not for direct use.
    /// </summary>
    /// <param name="host">The layout area host that owns the rendering stream.</param>
    /// <param name="context">The rendering context for the bar's area.</param>
    /// <param name="store">The entity store to update with rendered controls.</param>
    /// <returns>The updated store and the change set of the bar and its action.</returns>
    protected override EntityStoreAndUpdates Render(LayoutAreaHost host, RenderingContext context, EntityStore store)
    {
        var ret = base.Render(host, context, store);
        if (Action is null)
            return ret;
        var rendered = host.RenderArea(GetContextForArea(context, ActionArea), Action, ret.Store);
        return rendered with { Updates = ret.Updates.Concat(rendered.Updates) };
    }
}
