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
    [Inject] protected ThemeService themeService { get; set; } = null!;

    protected bool isDarkMode;

    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        isDarkMode = await IsDarkModeAsync();
        themeService.SetTheme(GetRadzenTheme());
    }

    protected string GetRadzenTheme()
    {
        return isDarkMode ? "standard-dark" : "standard";
    }
}
