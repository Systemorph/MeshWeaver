using OpenSmc.Data;
using OpenSmc.Layout.Composition;

namespace OpenSmc.Layout
{
    public interface IContainerControl : IUiControl
    {
        IContainerControl SetAreas(IReadOnlyCollection<string> areas);
        IEnumerable<(string Area, UiControl Control)> RenderSubAreas(LayoutAreaHost host, RenderingContext context);
    }
}
