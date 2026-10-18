using System.Reactive.Linq;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout;

/// <summary>
/// The outcome line of a click — "Sending invitation…", "Draft created.", "Couldn't connect: …" —
/// as DATA: an intent (<see cref="MessageBarIntent"/>) and a text. A click writes one into a data id
/// (<see cref="ActionNoticeExtensions.ShowNotice"/>) and the page renders it as a
/// <see cref="MessageBarControl"/> (<see cref="ActionNoticeExtensions.NoticeView"/>), so the banner
/// follows the theme and carries the intent's accessible role. It replaces the hand-styled
/// <c>&lt;p style="color:#4ade80"&gt;</c> strings pages used to store in that slot.
/// </summary>
/// <param name="Intent">One of <see cref="MessageBarIntent"/>.</param>
/// <param name="Text">The message, already localized; rendered as text, never as markup.</param>
public record ActionNotice(string Intent, string Text)
{
    /// <summary>A neutral notice — work in progress ("Checking out…") or a plain statement.</summary>
    /// <param name="text">The message.</param>
    /// <returns>An <see cref="MessageBarIntent.Info"/> notice.</returns>
    public static ActionNotice Info(string text) => new(MessageBarIntent.Info, text);

    /// <summary>The action succeeded.</summary>
    /// <param name="text">The message.</param>
    /// <returns>A <see cref="MessageBarIntent.Success"/> notice.</returns>
    public static ActionNotice Success(string text) => new(MessageBarIntent.Success, text);

    /// <summary>The action went through with a caveat.</summary>
    /// <param name="text">The message.</param>
    /// <returns>A <see cref="MessageBarIntent.Warning"/> notice.</returns>
    public static ActionNotice Warning(string text) => new(MessageBarIntent.Warning, text);

    /// <summary>The action failed, or could not start.</summary>
    /// <param name="text">The message.</param>
    /// <returns>An <see cref="MessageBarIntent.Error"/> notice.</returns>
    public static ActionNotice Error(string text) => new(MessageBarIntent.Error, text);

    /// <summary>The notice as the platform's banner.</summary>
    /// <returns>A <see cref="MessageBarControl"/> with this notice's intent and text.</returns>
    public MessageBarControl ToControl() => Controls.MessageBar(Text).WithIntent(Intent);
}

/// <summary>Writing and rendering an <see cref="ActionNotice"/> slot.</summary>
public static class ActionNoticeExtensions
{
    /// <summary>Writes <paramref name="notice"/> into the data id <paramref name="id"/>, replacing the previous one.</summary>
    /// <param name="host">The layout area host.</param>
    /// <param name="id">The data id the page's <see cref="NoticeView"/> reads.</param>
    /// <param name="notice">The notice to show.</param>
    public static void ShowNotice(this LayoutAreaHost host, string id, ActionNotice notice)
        => host.UpdateData(id, notice);

    /// <summary>
    /// The view of the notice slot <paramref name="id"/>: nothing (an empty stack) until a notice is
    /// written, then the latest notice as a <see cref="MessageBarControl"/>. Use as
    /// <c>stack.WithView((h, _) =&gt; h.NoticeView(id))</c>.
    /// </summary>
    /// <param name="host">The layout area host.</param>
    /// <param name="id">The data id written by <see cref="ShowNotice"/>.</param>
    /// <returns>The slot's controls, one per written notice.</returns>
    public static IObservable<UiControl?> NoticeView(this LayoutAreaHost host, string id)
        => host.Stream.GetDataStream<ActionNotice>(id)
            .Select(notice => notice is null || string.IsNullOrEmpty(notice.Text)
                ? (UiControl?)Controls.Stack.WithWidth("100%")
                : notice.ToControl())
            .StartWith((UiControl?)Controls.Stack.WithWidth("100%"));
}
