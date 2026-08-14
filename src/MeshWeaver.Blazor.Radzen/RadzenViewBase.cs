using MeshWeaver.Blazor;
using MeshWeaver.Layout;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Radzen;

namespace MeshWeaver.Blazor.Radzen;

/// <summary>
/// Base class for Radzen views that provides theme service functionality and self-loads the
/// pack's static assets (Radzen's base stylesheet + its classic interop script) on first render.
/// The shell (App.razor) carries NO Radzen tags — the pack is fully self-contained, which is what
/// makes it droppable/injectable as a view pack.
/// </summary>
public abstract class RadzenViewBase<TControl, TView> : BlazorView<TControl, TView>
    where TControl : UiControl
    where TView : RadzenViewBase<TControl, TView>
{
    /// <summary>Radzen theme service used to apply the light/dark theme to Radzen components.</summary>
    [Inject] protected ThemeService themeService { get; set; } = null!;

    /// <summary>Whether the current UI theme is dark mode.</summary>
    protected bool isDarkMode;

    /// <summary>
    /// True once Radzen's stylesheet and interop script are loaded in this document. Views gate
    /// their Radzen components on this so no Radzen JS interop fires before the script exists.
    /// The loader is memoized per document, so only the FIRST Radzen view on a page pays the load.
    /// </summary>
    protected bool AssetsReady { get; private set; }

    /// <summary>
    /// Initializes the view and applies the Radzen theme matching the active dark/light mode.
    /// </summary>
    /// <returns>A task that completes when initialization is finished.</returns>
    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        isDarkMode = await IsDarkModeAsync();
        themeService.SetTheme(GetRadzenTheme());
    }

    private bool assetsLoading;

    /// <summary>
    /// Loads the pack's static assets once per document, then re-renders with
    /// <see cref="AssetsReady"/> set so the Radzen components appear. NOT gated on
    /// <c>firstRender</c>: a failed load (network blip, circuit reconnect) is caught and logged,
    /// and the next render retries — the loader module evicts a failed promise, so retry is real.
    /// A first-render-only attempt would leave the view permanently blank after one transient
    /// failure (review finding on the introducing PR).
    /// </summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await base.OnAfterRenderAsync(firstRender);
        if (AssetsReady || assetsLoading)
            return;
        assetsLoading = true;
        try
        {
            var loader = await JSRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./_content/MeshWeaver.Blazor/assetLoader.js");
            await using (loader)
            {
                await loader.InvokeVoidAsync("ensure", "_content/Radzen.Blazor/css/material-base.css", "css");
                await loader.InvokeVoidAsync("ensure", "_content/Radzen.Blazor/Radzen.Blazor.js", "js");
            }
            AssetsReady = true;
            StateHasChanged();
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Radzen pack assets failed to load; retrying on the next render");
        }
        finally
        {
            assetsLoading = false;
        }
    }

    /// <summary>
    /// Returns the Radzen theme name corresponding to the current dark/light mode.
    /// </summary>
    /// <returns><c>standard-dark</c> in dark mode; otherwise <c>standard</c>.</returns>
    protected string GetRadzenTheme()
    {
        return isDarkMode ? "standard-dark" : "standard";
    }
}
