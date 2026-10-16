namespace MeshWeaver.Layout;

/// <summary>
/// <b>Copy link</b> and <b>Share</b> for one address. Copy writes <see cref="Url"/> to the clipboard
/// inside the click itself (a browser refuses a clipboard write that has left the user's gesture);
/// Share opens the device's own share sheet and is offered ONLY where the browser has one, so a
/// desktop browser without it shows Copy alone instead of a button that does nothing. The outcome of
/// either is announced in a polite live region. Both labels are the platform's own, in the viewer's
/// language.
///
/// <para>An empty <see cref="Url"/> renders both actions disabled, so a bound address that is cleared
/// (an invitation that closed) leaves nothing to copy or share.</para>
/// </summary>
/// <param name="Url">The absolute address to copy and share — a literal or a data-binding pointer.</param>
public record ShareLinkControl(object Url) : UiControl<ShareLinkControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>The title the share sheet shows — a literal or a binding; optional.</summary>
    public object? Title { get; init; }

    /// <summary>The text the share sheet sends along with the address — a literal or a binding; optional.</summary>
    public object? Text { get; init; }

    /// <summary>Sets the share sheet's title.</summary>
    /// <param name="title">The title, or a binding.</param>
    public ShareLinkControl WithTitle(object title) => this with { Title = title };

    /// <summary>Sets the text sent with the address.</summary>
    /// <param name="text">The text, or a binding.</param>
    public ShareLinkControl WithText(object text) => this with { Text = text };
}
