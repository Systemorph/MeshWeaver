namespace MeshWeaver.Layout;

public record Skin;
public record Skin<TSkin> : Skin
where TSkin: Skin<TSkin>
{
    protected TSkin This => (TSkin)this;
    public object Class { get; init; }
    public object Style { get; init; }
    public TSkin WithClass(object @class) => This with { Class = @class };

    //public const string Modal = nameof(Modal);
    //public const string ContextMenu = nameof(ContextMenu);
    //public const string Toolbar = nameof(Toolbar);
    //public const string MainWindow = nameof(MainWindow);
    //public const string SideMenu = nameof(SideMenu);
    //public const string HorizontalPanel = nameof(HorizontalPanel);
    //public const string HorizontalPanelEqualCols = nameof(HorizontalPanelEqualCols);
    //public const string VerticalPanel = nameof(VerticalPanel);
    //public const string GridLayout = nameof(GridLayout);
    //public const string Action = nameof(Action);
    //public const string Link = nameof(Link);
}
