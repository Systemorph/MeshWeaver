using MeshWeaver.Blazor;
using MeshWeaver.Layout;
using Microsoft.AspNetCore.Components;
using Radzen;

namespace MeshWeaver.Blazor.Radzen;

/// <summary>
/// Base class for Radzen views that provides theme service functionality.
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
    /// Initializes the view and applies the Radzen theme matching the active dark/light mode.
    /// </summary>
    /// <returns>A task that completes when initialization is finished.</returns>
    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        isDarkMode = await IsDarkModeAsync();
        themeService.SetTheme(GetRadzenTheme());
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
