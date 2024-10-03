namespace MeshWeaver.Layout;

/// <summary>
/// Provides constants for different layout areas.
/// </summary>
public static class Area
{
    /// <summary>
    /// Represents the side menu area.
    /// </summary>
    public const string SideMenu = nameof(SideMenu);

    /// <summary>
    /// Represents the main content area.
    /// </summary>
    public const string Main = nameof(Main);

    /// <summary>
    /// Represents the toolbar area.
    /// </summary>
    public const string Toolbar = nameof(Toolbar);

    /// <summary>
    /// Represents the context menu area.
    /// </summary>
    public const string ContextMenu = nameof(ContextMenu);

    /// <summary>
    /// Represents the status bar area.
    /// </summary>
    public const string StatusBar = nameof(StatusBar);

    /// <summary>
    /// Represents the modal area.
    /// </summary>
    public const string Modal = nameof(Modal);

    /// <summary>
    /// Represents the window area.
    /// </summary>
    public const string Window = nameof(Window);

    /// <summary>
    /// Gets a read-only collection of all layout areas.
    /// </summary>
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
