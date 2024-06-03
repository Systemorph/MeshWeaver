using OpenSmc.Application.Styles;

namespace OpenSmc.Layout.Views;

public abstract record NavItem<TControl>(
    string ModuleName,
    string ApiVersion,
    object Data
) : UiControl<TControl>(ModuleName, ApiVersion, Data)
    where TControl : NavItem<TControl>
{
    public string Title { get; set; }

    public string Href { get; set; }

    public Icon Icon { get; set; }

    public TControl WithTitle(string title) => (TControl) (this with { Title = title });

    public TControl WithHref(string href) => (TControl) (this with { Href = href });

    public TControl WithIcon(Icon icon) => (TControl) (this with { Icon = icon });
}
