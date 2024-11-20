﻿using MeshWeaver.Demo.ViewModel.CkeckBox;
using MeshWeaver.Demo.ViewModel.DropDown;
using MeshWeaver.Demo.ViewModel.ItemTemplate;
using MeshWeaver.Demo.ViewModel.Listbox;
using MeshWeaver.Domain.Layout;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Domain;
using MeshWeaver.Messaging;

namespace MeshWeaver.Demo.ViewModel;

/// <summary>
/// Provides a centralized registration mechanism for all Demo application views and configurations. This static class facilitates the addition of various views created for Demo purposes and documentation to the application's MessageHub configuration.
/// </summary>
public static class DemoViewModelsRegistry
{
    /// <summary>
    /// Registers all Demo views and configurations to the provided MessageHub configuration.
    /// </summary>
    /// <param name="configuration">The MessageHub configuration to be enhanced with Demo views and settings.</param>
    /// <returns>The updated MessageHub configuration with Demo views and documentation added.</returns>
    /// <remarks>
    /// This method sequentially adds some examples of Demo views to the application layout. It also configures the application menu and default views, and includes Demo-specific documentation.
    /// </remarks>
    public static MessageHubConfiguration AddDemoViewModels(
        this MessageHubConfiguration configuration
    )
        => configuration
            .AddDemoDocumentation()
            .AddLayout(layout => layout
                .AddViewModelStateDemo()
                .AddSelectControlDemo()
                .AddListboxDemo()
                .AddCheckboxDemo()
                .AddCurrenciesItemTemplateDemo()
            )
            ;
}
