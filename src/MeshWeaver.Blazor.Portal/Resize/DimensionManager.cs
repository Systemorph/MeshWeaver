// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace MeshWeaver.Blazor.Portal.Resize;

/// <summary>
/// Shared, circuit-scoped broadcaster of the current browser viewport. Holds the latest
/// size and classification and raises events so UI and non-UI listeners stay in sync.
/// </summary>
public class DimensionManager
{
    private ViewportInformation viewportInformation = new ViewportInformation(false, false, false);
    private ViewportSize viewportSize = new ViewportSize(0, 0);

    /// <summary>
    /// Raised whenever the raw viewport pixel size changes.
    /// </summary>
    public event ViewportSizeChangedEventHandler? OnViewportSizeChanged;

    /// <summary>
    /// Raised whenever the viewport classification (desktop/mobile, ultra-low) changes.
    /// </summary>
    public event ViewportInformationChangedEventHandler? OnViewportInformationChanged;

    /// <summary>
    /// The most recently reported viewport classification.
    /// </summary>
    public ViewportInformation ViewportInformation => viewportInformation;

    /// <summary>
    /// The most recently reported viewport pixel size.
    /// </summary>
    public ViewportSize ViewportSize => viewportSize;

    internal void InvokeOnViewportSizeChanged(ViewportSize newViewportSize)
    {
        viewportSize = newViewportSize;
        OnViewportSizeChanged?.Invoke(this, new ViewportSizeChangedEventArgs(newViewportSize));
    }

    internal void InvokeOnViewportInformationChanged(ViewportInformation newViewportInformation)
    {
        viewportInformation = newViewportInformation;
        OnViewportInformationChanged?.Invoke(this, new ViewportInformationChangedEventArgs(newViewportInformation));
    }
}

/// <summary>
/// Handles a change in the viewport classification.
/// </summary>
/// <param name="sender">The <c>DimensionManager</c> raising the event.</param>
/// <param name="e">Details of the new viewport classification.</param>
public delegate void ViewportInformationChangedEventHandler(object sender, ViewportInformationChangedEventArgs e);

/// <summary>
/// Handles a change in the viewport pixel size.
/// </summary>
/// <param name="sender">The <c>DimensionManager</c> raising the event.</param>
/// <param name="e">Details of the new viewport size.</param>
public delegate void ViewportSizeChangedEventHandler(object sender, ViewportSizeChangedEventArgs e);

/// <summary>
/// Event data carrying the updated viewport classification.
/// </summary>
/// <param name="viewportInformation">The new viewport classification.</param>
public class ViewportInformationChangedEventArgs(ViewportInformation viewportInformation) : EventArgs
{
    /// <summary>
    /// The new viewport classification.
    /// </summary>
    public ViewportInformation ViewportInformation { get; } = viewportInformation;
}

/// <summary>
/// Event data carrying the updated viewport pixel size.
/// </summary>
/// <param name="viewportSize">The new viewport size.</param>
public class ViewportSizeChangedEventArgs(ViewportSize viewportSize) : EventArgs
{
    /// <summary>
    /// The new viewport size.
    /// </summary>
    public ViewportSize ViewportSize { get; } = viewportSize;
}
