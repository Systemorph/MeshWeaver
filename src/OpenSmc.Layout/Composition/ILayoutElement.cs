namespace OpenSmc.Layout.Composition;

public interface IUiControlWithSubAreas : IUiControl
{
    IReadOnlyCollection<LayoutArea> SubAreas { get; }
    IUiControlWithSubAreas SetArea(LayoutArea areaChanged);
}
