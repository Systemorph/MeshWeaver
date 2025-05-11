using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using static MeshWeaver.Layout.Controls;

namespace MeshWeaver.Documentation.LayoutAreas
{
    /// <summary>
    /// Demo how to use the file browser control.
    /// </summary>
    public static class FileBrowserLayoutArea
    {
        /// <summary>
        /// Adds the file browser demo to the layout.
        /// </summary>
        /// <param name="layout"></param>
        /// <returns></returns>
        public static LayoutDefinition AddFileBrowser(this LayoutDefinition layout)
            => layout.WithView(nameof(FileBrowser), FileBrowser);

        private static UiControl FileBrowser(LayoutAreaHost host, RenderingContext ctx)
        {
            return Stack
                .WithView(H2("File Browser").WithStyle(style => style.WithMarginBottom("1em")))
                .WithView(Controls
                        .FileBrowser("Documentation", "DemoFolder").CreatePath().WithTopLevel("DemoFolder"));
        }
    }
}
