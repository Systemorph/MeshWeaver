// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using MeshWeaver.Layout.Views;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components.Icons.Regular;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace MeshWeaverApp1.Portal.Layout;

public partial class DesktopNavMenu : ComponentBase
{
    public static Icon BlogIcon(bool active = false)
        => active
            ? new Icons.Filled.Size24.Book()
            : new Size24.Book();
    public static Icon ArticlesIcon(bool active = false)
        => active
            ? new Icons.Filled.Size24.Book()
            : new Size24.Book();
    public static Icon DocumentationIcon(bool active = false)
        => active
            ? new Icons.Filled.Size24.DocumentCube()
            : new Size24.DocumentCube();
    public static Icon CollectionsIcon(bool active = false)
        => active
            ? new Icons.Filled.Size24.DocumentFolder()
            : new Icons.Regular.Size24.DocumentFolder();
    public static Icon AgentsIcon(bool active = false)
        => active
            ? new Icons.Filled.Size24.Bot()
            : new Icons.Regular.Size24.Bot();
    public static Icon TodoIcon(bool active = false)
        => active
            ? new Icons.Filled.Size24.TasksApp()
            : new Icons.Regular.Size24.TasksApp();
			
    public static Icon ChatIcon(bool active = false)
        => active
            ? new Icons.Filled.Size24.Chat()
            : new Icons.Regular.Size24.Chat();

    public static Icon DocumentationLayoutAreaIcon(bool active = false)
        => active
            ? new Icons.Filled.Size24.AppGeneric()
            : new Icons.Regular.Size24.AppGeneric();

    public static string LayoutAreas(string app)
        => $"app/{app}/{LayoutAreaCatalogArea.LayoutAreas}";


    public static Icon NorthwindLayoutAreaIcon(bool active = false)
        => active
            ? new Icons.Filled.Size24.ShoppingBag()
            : new Icons.Regular.Size24.ShoppingBag();
}
