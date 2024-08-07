namespace MeshWeaver.Layout;

public static class Area
{
    public const string SideMenu = nameof(SideMenu);
    public const string Main = nameof(Main);
    public const string Toolbar = nameof(Toolbar);
    public const string ContextMenu = nameof(ContextMenu);
    public const string StatusBar = nameof(StatusBar);
    public const string Modal = nameof(Modal);
    public const string Window = nameof(Window);

    internal static readonly IReadOnlyCollection<string> All = new HashSet<string>
    {
        SideMenu,
        Toolbar,
        Main,
        ContextMenu,
        StatusBar,
        Modal
    };
}
