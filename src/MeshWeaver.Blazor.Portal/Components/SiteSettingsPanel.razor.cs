using MeshWeaver.Blazor.Portal.Infrastructure;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components.Extensions;

namespace MeshWeaver.Blazor.Portal.Components;

/// <summary>
/// Panel letting the user adjust site appearance — theme mode, accent color, and text
/// direction — and reset all site settings by clearing the cache and stored theme.
/// </summary>
public partial class SiteSettingsPanel
{
    private string? status;
    private bool popVisible;
    private bool ltr = true;
    private FluentDesignTheme? theme;

    /// <summary>Logger for recording site-settings actions such as a reset.</summary>
    [Inject] public required ILogger<SiteSettingsPanel> Logger { get; set; }

    /// <summary>Accessor used to clear the browser cache storage when resetting the site.</summary>
    [Inject] public required CacheStorageAccessor CacheStorageAccessor { get; set; }

    /// <summary>Shared global UI state; supplies the current text direction.</summary>
    [Inject] public required GlobalState GlobalState { get; set; }

    /// <summary>The selected design theme mode (light, dark, or system).</summary>
    public DesignThemeModes Mode { get; set; }

    /// <summary>The selected accent (Office) color, or <c>null</c> for the default.</summary>
    public OfficeColor? OfficeColor { get; set; }

    /// <summary>The selected text direction (left-to-right or right-to-left), or <c>null</c> for default.</summary>
    public LocalizationDirection? Direction { get; set; }


    /// <summary>
    /// On the first render, seeds the direction toggle from the shared global state.
    /// </summary>
    /// <param name="firstRender"><c>true</c> on the component's first render pass.</param>
    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender)
        {
            Direction = GlobalState.Dir;
            ltr = !Direction.HasValue || Direction.Value == LocalizationDirection.LeftToRight;
        }
    }

    /// <summary>
    /// Updates the panel's text direction in response to the direction toggle.
    /// </summary>
    /// <param name="isLeftToRight"><c>true</c> for left-to-right, <c>false</c> for right-to-left.</param>
    protected void HandleDirectionChanged(bool isLeftToRight)
    {

        ltr = isLeftToRight;
        Direction = isLeftToRight ? LocalizationDirection.LeftToRight : LocalizationDirection.RightToLeft;
    }

    private async Task ResetSiteAsync()
    {
        var msg = "Site settings reset and cache cleared!";

        await CacheStorageAccessor.RemoveAllAsync();
        theme?.ClearLocalStorageAsync();

        Logger.LogInformation(msg);
        status = msg;

        OfficeColor = OfficeColorUtilities.GetRandom();
        Mode = DesignThemeModes.System;
    }

    private static string? GetCustomColor(OfficeColor? color)
    {
        return color switch
        {
            null => OfficeColorUtilities.GetRandom().ToAttributeValue(),
            Microsoft.FluentUI.AspNetCore.Components.OfficeColor.Default => "#036ac4",
            _ => color.ToAttributeValue(),
        };

    }
}
