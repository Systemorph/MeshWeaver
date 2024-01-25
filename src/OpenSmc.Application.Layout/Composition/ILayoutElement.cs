using OpenSmc.Layout;

namespace OpenSmc.Application.Layout.Composition;

public interface IUiControlWithSubAreas : IUiControl
{
    IReadOnlyCollection<AreaChangedEvent> SubAreas { get; }
    IUiControlWithSubAreas SetArea(AreaChangedEvent areaChanged);
}
