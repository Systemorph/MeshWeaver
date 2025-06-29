using MeshWeaver.Data;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Layout;

/// <summary>
/// Represents a dialog control that can display modal dialogs with content
/// </summary>
public record DialogControl
    : UiControl<DialogControl>
{
    internal DialogControl(object content)
        : base(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
    {
        Content = content;
    }


    public DialogControl()
        : this("Dialog content")
    {
    }
    /// <summary>
    /// Standard area name for dialogs in the layout system
    /// </summary>
    public const string DialogArea = "$Dialog";


    /// <summary>
    /// The title of the dialog
    /// </summary>
    public object Title { get; init; } = "Dialog";

    /// <summary>
    /// The content to display in the dialog
    /// </summary>
    internal object Content { get; init; }


    /// <summary>
    /// Content area to be rendered.
    /// </summary>
    public NamedAreaControl ContentArea { get; init; } =
        new($"{DialogArea}/{nameof(ContentArea)}") { ShowProgress = true };

    public DialogControl WithContent(object content)
        => this with { Content = content };

    /// <summary>
    /// Whether the dialog can be closed with the X button
    /// </summary>
    public bool IsClosable { get; init; } = true;

    /// <summary>
    /// The size of the dialog (S, M, L)
    /// </summary>
    public string Size { get; init; } = "M";

    /// <summary>
    /// Callback when dialog is closed
    /// </summary>
    internal Func<DialogCloseActionContext, Task> CloseAction { get; init; }

    /// <summary>
    /// Sets the title of the dialog
    /// </summary>
    public DialogControl WithTitle(string title) => this with { Title = title };

    /// <summary>
    /// Sets whether the dialog is closable
    /// </summary>
    public DialogControl WithClosable(bool closable) => this with { IsClosable = closable };

    /// <summary>
    /// Sets the size of the dialog
    /// </summary>
    public DialogControl WithSize(string size) => this with { Size = size };

    /// <summary>
    /// Sets the close action for the dialog
    /// </summary>
    public DialogControl WithCloseAction(Func<DialogCloseActionContext, Task> onClose) => this with { CloseAction = onClose };

    /// <summary>
    /// Sets the close action for the dialog
    /// </summary>
    public DialogControl WithCloseAction(Action<DialogCloseActionContext> onClose) =>
        WithCloseAction(c =>
        {
            onClose(c);
            return Task.CompletedTask;
        });

    protected override EntityStoreAndUpdates Render(LayoutAreaHost host, RenderingContext context, EntityStore store)
    {
        var ret = base.Render(host, context, store);

        // If Content is a UiControl, render it in the ContentArea like ItemTemplateControl does
        if (Content is UiControl contentControl)
        {
            var renderedContent = host.RenderArea(GetContextForArea(context,nameof(ContentArea)), contentControl, ret.Store);
            return renderedContent with { Updates = ret.Updates.Concat(renderedContent.Updates) };
        }

        // Otherwise, return as-is for non-UiControl content (strings, etc.)
        return ret;
    }



}
