using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Utils;
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

        /// <summary>
        /// Creates a file browser control that displays a demo folder structure.
        /// </summary>
        /// <param name="host">The layout area host to which the file browser will be added.</param>
        /// <param name="ctx">The rendering context for the view.</param>
        /// <returns>A UI control containing the file browser demo with a header and file browser component.</returns>
        private static UiControl FileBrowser(LayoutAreaHost host, RenderingContext ctx)
        {
            var collection = host.Reference.Id?.ToString()!.Split('/').FirstOrDefault();
            if (string.IsNullOrEmpty(collection))
                return Controls.Markdown("No collection specified.");

            return Stack
                .WithView(H2(collection.Wordify()).WithStyle(style => style.WithMarginBottom("1em")))
                .WithView(Controls
                        .FileBrowser(collection, "/").CreatePath());
        }
    }
}
