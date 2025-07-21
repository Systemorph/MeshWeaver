// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace MeshWeaver.Portal.Shared.Web.Resize;

public class DimensionManager
{
    private ViewportInformation viewportInformation = new ViewportInformation(false, false, false);
    private ViewportSize viewportSize = new ViewportSize(0, 0);

    public event ViewportSizeChangedEventHandler? OnViewportSizeChanged;
    public event ViewportInformationChangedEventHandler? OnViewportInformationChanged;

    public ViewportInformation ViewportInformation => viewportInformation;
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

public delegate void ViewportInformationChangedEventHandler(object sender, ViewportInformationChangedEventArgs e);
public delegate void ViewportSizeChangedEventHandler(object sender, ViewportSizeChangedEventArgs e);

public class ViewportInformationChangedEventArgs(ViewportInformation viewportInformation) : EventArgs
{
    public ViewportInformation ViewportInformation { get; } = viewportInformation;
}

public class ViewportSizeChangedEventArgs(ViewportSize viewportSize) : EventArgs
{
    public ViewportSize ViewportSize { get; } = viewportSize;
}
