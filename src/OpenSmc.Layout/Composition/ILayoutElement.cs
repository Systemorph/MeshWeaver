﻿namespace OpenSmc.Layout.Composition;

public interface IUiControlWithSubAreas : IUiControl
{
    IReadOnlyCollection<AreaChangedEvent> SubAreas { get; }
    IUiControlWithSubAreas SetArea(AreaChangedEvent areaChanged);
}