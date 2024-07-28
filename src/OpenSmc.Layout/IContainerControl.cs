using OpenSmc.Data;
using OpenSmc.Layout.Composition;

namespace OpenSmc.Layout
{
    public interface IContainerControl : IUiControl
    {
        IContainerControl SetParentArea(string parent);
        IEnumerable<(string Area, UiControl Control)> RenderSubAreas(LayoutAreaHost host, RenderingContext context);
    }
}
