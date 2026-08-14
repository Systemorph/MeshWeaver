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
public class BrowserDimensionWatcher : ComponentBase, IAsyncDisposable
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
    /// The JS module this component's interop goes through. See <see cref="OnAfterRenderAsync"/>
    /// for why the module reference — not the <c>window.*</c> globals it also assigns — is what
    /// this component may depend on.
    /// </summary>
    private IJSObjectReference? module;

    /// <summary>Handed to JS for the resize callback; released in <see cref="DisposeAsync"/>.</summary>
    private DotNetObjectReference<BrowserDimensionWatcher>? selfReference;

    /// <summary>
    /// Identifies THIS watcher's resize listener on the JS side, so <see cref="DisposeAsync"/> can
    /// detach exactly it. Releasing <see cref="selfReference"/> alone is not enough: the listener
    /// closes over that reference, so one left attached keeps firing on every resize and invokes a
    /// method on a released .NET object — console errors, and the object pinned for the page's life.
    /// </summary>
    private readonly string resizeToken = Guid.NewGuid().ToString("N");

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
                // 🚨 IMPORT THE MODULE, don't reach for the globals (#1572).
                //
                // `getWindowDimensions` / `listenToWindowResize` are EXPORTS of
                // PortalLayoutBase.razor.js, which additionally assigns them onto `window` as a
                // side effect OF BEING IMPORTED. That import is lazy and belongs to a different
                // component (PortalLayoutBase.EnsureJsModuleAsync), while this watcher lives in
                // Routes.razor and runs its own first render — so nothing orders the two. When
                // this render won, `window.getWindowDimensions` was still undefined and the
                // interop threw JSException ("The value 'window.getWindowDimensions' is not a
                // function"), which kills the Blazor circuit for that user.
                //
                // Importing here removes the ordering dependency instead of tolerating it: ES
                // module imports are idempotent and cached, so this costs nothing when the layout
                // already pulled it in, and it is correct when it did not. The window.* aliases
                // stay in the JS for any in-mesh caller the compiler cannot see — this component
                // simply no longer relies on someone else having triggered them.
                module ??= await JS.InvokeAsync<IJSObjectReference>(
                    "import", "./_content/MeshWeaver.Blazor.Portal/Layout/PortalLayoutBase.razor.js");

                var viewportSize = await module.InvokeAsync<ViewportSize>("getWindowDimensions");
                DimensionManager.InvokeOnViewportSizeChanged(viewportSize);
                ViewportInformation = ViewportInformation.GetViewportInformation(viewportSize);
                DimensionManager.InvokeOnViewportInformationChanged(ViewportInformation);

                await ViewportInformationChanged.InvokeAsync(ViewportInformation);

                selfReference ??= DotNetObjectReference.Create(this);
                await module.InvokeVoidAsync("listenToWindowResize", selfReference, resizeToken);
            }
            catch (JSDisconnectedException)
            {
                // Circuit disconnected during prerender or navigation - this is expected
            }
        }

        await base.OnAfterRenderAsync(firstRender);
    }

    /// <summary>
    /// Detaches the browser resize listener, then releases the JS module reference and the .NET
    /// object reference it closed over — in that order, because releasing the reference first
    /// would leave a live listener calling into a released object.
    /// <see cref="JSDisconnectedException"/> is expected here: on a circuit that has already gone
    /// away there is nothing left to detach or release.
    /// </summary>
    /// <returns>A task that completes once the listener is detached and both references released.</returns>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (module is not null)
            {
                await module.InvokeVoidAsync("stopListeningToWindowResize", resizeToken);
                await module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
            // The circuit is gone; the browser-side module and its listeners went with it.
        }
        finally
        {
            module = null;
            selfReference?.Dispose();
            selfReference = null;
        }
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
