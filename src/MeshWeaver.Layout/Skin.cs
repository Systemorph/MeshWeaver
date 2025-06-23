﻿namespace MeshWeaver.Layout;

/// <summary>
/// Represents the base class for skins.
/// </summary>
public abstract record Skin;

/// <summary>
/// Represents a generic skin with customizable properties.
/// </summary>
/// <typeparam name="TSkin">The type of the skin.</typeparam>
public abstract record Skin<TSkin> : Skin
where TSkin : Skin<TSkin>
{
    /// <summary>
    /// Gets the current instance of the skin.
    /// </summary>
    protected TSkin This => (TSkin)this;


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
