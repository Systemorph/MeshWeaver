using System.Reactive.Linq;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// Registers the "Welcome" area for anonymous/virtual users.
/// Shows a marketing landing page with a chat widget and sign-in button.
/// </summary>
public static class WelcomeLayoutArea
{
    public const string WelcomeArea = "Welcome";

    /// <summary>
    /// Adds the Welcome view to the default layout areas.
    /// </summary>
    public static LayoutDefinition AddWelcomeArea(this LayoutDefinition layout)
        => layout.WithView(WelcomeArea, Welcome);

    /// <summary>
    /// Renders the anonymous welcome page.
    /// </summary>
    public static UiControl Welcome(LayoutAreaHost host, RenderingContext _)
    {
        var container = Controls.Stack.WithWidth("100%")
            .WithStyle("max-width: 800px; margin: 0 auto; padding: 48px 24px; text-align: center;");

        // Marketing header
        container = container.WithView(Controls.Html(
            "<h1 style=\"font-size: 2.5rem; margin: 0 0 16px 0;\">Welcome to MeshWeaver</h1>"));

        // Description
        container = container.WithView(Controls.Html(
            "<p style=\"font-size: 1.1rem; color: var(--neutral-foreground-hint); max-width: 600px; margin: 0 auto 32px auto;\">" +
            "Your collaborative knowledge platform. Organize documents, manage approvals, " +
            "and work together seamlessly with AI-powered assistance." +
            "</p>"));

        // Chat widget
        var chatSection = Controls.Stack.WithWidth("100%")
            .WithStyle("text-align: left; margin-bottom: 32px; padding: 16px; border: 1px solid var(--neutral-stroke-rest); border-radius: 8px; background: var(--neutral-layer-2);");

        chatSection = chatSection.WithView(Controls.Html(
            "<h3 style=\"margin: 0 0 12px 0;\">Try it out — ask anything</h3>"));

        var chatControl = new ThreadChatControl()
            .WithInitialContextDisplayName("Welcome");

        chatSection = chatSection.WithView(chatControl);
        container = container.WithView(chatSection);

        // Sign-in button
        container = container.WithView(Controls.Html(
            "<div style=\"margin-top: 16px;\">" +
            "<a href=\"/login\" style=\"display: inline-block; padding: 12px 32px; background: var(--accent-fill-rest); color: white; border-radius: 6px; text-decoration: none; font-weight: 600;\">Sign in to get started</a>" +
            "</div>"));

        return container;
    }
}
