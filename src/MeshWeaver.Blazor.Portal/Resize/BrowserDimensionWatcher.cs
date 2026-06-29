// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MeshWeaver.Blazor.Portal.Resize;

/// <summary>
/// Invisible component that measures the browser window via JS interop on first render,
/// listens for resize events, and publishes the resulting viewport size/classification
/// through the shared <c>DimensionManager</c> and a two-way bound parameter.
/// </summary>
public class BrowserDimensionWatcher : ComponentBase
{
    /// <summary>
    /// The current viewport classification (desktop/mobile, ultra-low height/width). Two-way bound.
    /// </summary>
    [Parameter]
    public ViewportInformation? ViewportInformation { get; set; }

    /// <summary>
    /// Raised when the viewport classification changes, supporting two-way binding of <c>ViewportInformation</c>.
    /// </summary>
    [Parameter]
    public EventCallback<ViewportInformation> ViewportInformationChanged { get; set; }

    /// <summary>
    /// JS runtime used to read window dimensions and subscribe to browser resize events.
    /// </summary>
    [Inject]
    public required IJSRuntime JS { get; init; }

    /// <summary>
    /// Shared manager that broadcasts viewport size and classification changes to non-UI listeners.
    /// </summary>
    [Inject]
    public required DimensionManager DimensionManager { get; init; }

    /// <summary>
    /// On first render, reads the initial window dimensions, publishes them, and registers
    /// the browser resize listener.
    /// </summary>
    /// <param name="firstRender">True on the component's first render pass.</param>
    /// <returns>A task that completes once first-render measurement and wiring finish.</returns>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            try
            {
                var viewportSize = await JS.InvokeAsync<ViewportSize>("window.getWindowDimensions");
                DimensionManager.InvokeOnViewportSizeChanged(viewportSize);
                ViewportInformation = ViewportInformation.GetViewportInformation(viewportSize);
                DimensionManager.InvokeOnViewportInformationChanged(ViewportInformation);

                await ViewportInformationChanged.InvokeAsync(ViewportInformation);

                await JS.InvokeVoidAsync("window.listenToWindowResize", DotNetObjectReference.Create(this));
            }
            catch (JSDisconnectedException)
            {
                // Circuit disconnected during prerender or navigation - this is expected
            }
        }

        await base.OnAfterRenderAsync(firstRender);
    }

    /// <summary>
    /// Invoked from JavaScript when the browser window is resized; publishes the new size and,
    /// only when the viewport classification actually changes, notifies listeners.
    /// </summary>
    /// <param name="viewportSize">The new window size reported by the browser.</param>
    /// <returns>A task that completes once listeners have been notified.</returns>
    [JSInvokable]
    public async Task OnResizeAsync(ViewportSize viewportSize)
    {
        DimensionManager.InvokeOnViewportSizeChanged(viewportSize);

        var newViewportInformation = ViewportInformation.GetViewportInformation(viewportSize);

        if (newViewportInformation.IsDesktop != ViewportInformation!.IsDesktop
            || newViewportInformation.IsUltraLowHeight != ViewportInformation.IsUltraLowHeight
            || newViewportInformation.IsUltraLowWidth != ViewportInformation.IsUltraLowWidth)
        {
            ViewportInformation = newViewportInformation;
            // A re-render happens on components after ViewportInformationChanged is invoked
            // we should invoke InvokeOnViewportInformationChanged first so that listeners of it
            // that are outside of the UI tree have the current viewport kind internally when components
            // call them
            DimensionManager.InvokeOnViewportInformationChanged(newViewportInformation);
            await ViewportInformationChanged.InvokeAsync(newViewportInformation);
        }
    }
}

/// <summary>
/// The pixel dimensions of the browser viewport.
/// </summary>
/// <param name="Width">The viewport width in pixels.</param>
/// <param name="Height">The viewport height in pixels.</param>
public record ViewportSize(int Width, int Height);
