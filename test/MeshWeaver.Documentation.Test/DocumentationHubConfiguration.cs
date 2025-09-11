using MeshWeaver.Layout;
using MeshWeaver.Messaging;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// All configurations for the test project
/// </summary>
public static class DocumentationHubConfiguration
{
    internal const string HtmlView = nameof(HtmlView);
    /// <summary>
    /// Configuration of the test host
    /// </summary>
    /// <param name="configuration"></param>
    /// <returns></returns>
    public static MessageHubConfiguration ConfigureDocumentationTestHost(this MessageHubConfiguration configuration)
    {
        return configuration
                .AddLayout(layout => layout
                    .WithView(HtmlView, Controls.Html("Hello World"))
                )
            ;
    }
}
