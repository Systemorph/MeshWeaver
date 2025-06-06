﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using MeshWeaver.Layout.Views;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace MeshWeaver.Portal.Shared.Web.Layout;

public partial class DesktopNavMenu : ComponentBase
{
    public static Icon ArticlesIcon(bool active = false)
        => active
            ? new Icons.Filled.Size24.Book()
            : new Icons.Regular.Size24.Book();
    public static Icon CollectionsIcon(bool active = false)
        => active
            ? new Icons.Filled.Size24.DocumentFolder()
            : new Icons.Regular.Size24.DocumentFolder();

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
