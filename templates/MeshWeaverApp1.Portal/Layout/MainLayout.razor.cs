﻿using MeshWeaver.AI;
using MeshWeaver.Blazor.Chat;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Messaging;
using MeshWeaverApp1.Portal.Components;
using MeshWeaverApp1.Portal.Resize;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MeshWeaverApp1.Portal.Layout;

public partial class MainLayout : IDisposable
{
    [Inject] private IJSRuntime JSRuntime { get; set; } = null!;
    [Inject] private NavigationManager NavigationManager { get; set; } = null!;

    private const string MessageBarSection = "MessagesTop";

    private bool isNavMenuOpen;
    private AgentChatControl chatControl = new AgentChatControl();

    protected override void OnInitialized()
    {
        base.OnInitialized();
        NavigationManager.LocationChanged += OnLocationChanged;
        UpdateChatContext();
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        UpdateChatContext();
        StateHasChanged();
    }

    private void UpdateChatContext()
    {
        var context = GetContextFromUrl();
        if (context != null)
        {
            chatControl = chatControl.WithContext(context);
        }
    }

    private AgentContext? GetContextFromUrl()
    {
        var path = NavigationManager.ToBaseRelativePath(NavigationManager.Uri);

        // Skip if path is empty or just "chat"
        if (string.IsNullOrEmpty(path) || path == "chat")
            return null;

        // Split the path into segments
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Need at least addressType and addressId
        if (segments.Length < 2)
            return null;

        var addressType = segments[0];
        var addressId = segments[1];

        // Create the Address with the extracted values
        var address = new Address(addressType, addressId);

        var layoutArea = segments.Length == 2 ? null : new LayoutAreaReference(segments[2])
        {
            Id = string.Join('/', segments.Skip(3))
        };

        // Create a new AgentContext with the extracted values
        return new AgentContext
        {
            Address = address,
            LayoutArea = layoutArea
        };
    }
    protected override void OnParametersSet()
    {
        if (ViewportInformation.IsDesktop && isNavMenuOpen)
        {
            isNavMenuOpen = false;
            CloseMobileNavMenu();
        }
    }
    [CascadingParameter]
    public required ViewportInformation ViewportInformation { get; set; }
    private void CloseMobileNavMenu()
    {
        isNavMenuOpen = false;
        StateHasChanged();
    }
    private IDialogReference? dialog;

    private async Task OpenSiteSettingsAsync()
    {
        dialog = await DialogService.ShowPanelAsync<SiteSettingsPanel>(new DialogParameters()
        {
            ShowTitle = true,
            Title = "Site settings",
            Alignment = Microsoft.FluentUI.AspNetCore.Components.HorizontalAlignment.Right,
            PrimaryAction = "OK",
            SecondaryAction = null,
            ShowDismiss = true
        });

        await dialog.Result;
    }
    public bool IsAIChatVisible { get; private set; }
    private AgentChatView? chatComponent;

    public void ToggleAIChatVisibility()
    {
        IsAIChatVisible = !IsAIChatVisible;
        StateHasChanged();
    }
    private async Task StartResize()
    {
        // Call the JavaScript function to handle the resize operation
        await JSRuntime.InvokeVoidAsync("chatResizer.startResize");
    }

    private async Task HandleNewChatAsync()
    {
        if (chatComponent != null)
        {
            await chatComponent.ResetConversationAsync();
        }
    }

    public void Dispose()
    {
        NavigationManager.LocationChanged -= OnLocationChanged;
    }
}
