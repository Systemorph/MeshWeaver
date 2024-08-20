namespace MeshWeaver.Layout;

public record BadgeControl(object Data)
    : UiControl<BadgeControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion, Data)
{
    public object Appearance { get; init; }
    public object BackgroundColor { get; init; }
    public object Circular { get; init; }
    public object Color { get; init; }
    public object DismissIcon { get; init; }
    public object DismissTitle { get; init; }
    public object Fill { get; init; }
    public object Height { get; init; }
    public object Width { get; init; }

    public BadgeControl WithAppearance(object appearance) => This with { Appearance = appearance };

    public BadgeControl WithBackgroundColor(object backgroundColor) => This with { BackgroundColor = backgroundColor };

    public BadgeControl WithCircular(object circular) => This with { Circular = circular };

    public BadgeControl WithColor(object color) => This with { Color = color };

    public BadgeControl WithDismissIcon(object dismissIcon) => This with { DismissIcon = dismissIcon };

    public BadgeControl WithDismissTitle(object dismissTitle) => This with { DismissTitle = dismissTitle };

    public BadgeControl WithFill(object fill) => This with { Fill = fill };

    public BadgeControl WithHeight(object height) => This with { Height = height };

    public BadgeControl WithWidth(object width) => This with { Width = width };
}
