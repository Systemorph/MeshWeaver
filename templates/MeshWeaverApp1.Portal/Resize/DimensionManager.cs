// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace MeshWeaverApp1.Portal.Resize;

public class DimensionManager
{
    private ViewportInformation _viewportInformation = new ViewportInformation(false, false, false);
    private ViewportSize _viewportSize = new ViewportSize(0, 0);

    public event ViewportSizeChangedEventHandler? OnViewportSizeChanged;
    public event ViewportInformationChangedEventHandler? OnViewportInformationChanged;

    public ViewportInformation ViewportInformation => _viewportInformation;
    public ViewportSize ViewportSize => _viewportSize;

    internal void InvokeOnViewportSizeChanged(ViewportSize newViewportSize)
    {
        _viewportSize = newViewportSize;
        OnViewportSizeChanged?.Invoke(this, new ViewportSizeChangedEventArgs(newViewportSize));
    }

    internal void InvokeOnViewportInformationChanged(ViewportInformation newViewportInformation)
    {
        _viewportInformation = newViewportInformation;
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
