using MeshWeaver.Application.Styles;
using MeshWeaver.Domain;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Memex.Portal.Shared.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Portal.Shared.Settings;

/// <summary>
/// The person app's <b>Security</b> tab — how this person confirms approvals
/// (<c>Doc/Architecture/ApprovalStepUp</c>). Framework controls only: a title, the explanation, and
/// one button that opens the enrolment page. The enrolment itself runs on that page, because the
/// WebAuthn ceremony must run in the page that asks for it (a full page load, not a circuit hop).
/// </summary>
public static class StepUpSettingsTab
{
    /// <summary>The tab id.</summary>
    public const string TabId = "Security";

    /// <summary>Registers the tab on the person app.</summary>
    /// <param name="config">The hub configuration.</param>
    /// <returns>The configuration.</returns>
    public static MessageHubConfiguration AddStepUpSettingsTab(this MessageHubConfiguration config) =>
        config.AddPersonAppTab(
            new SettingsMenuItemDefinition(
                Id: TabId,
                Label: "Security",
                ContentBuilder: BuildContent,
                Icon: FluentIcons.ShieldLock(),
                Order: PersonApp.SecurityOrder,
                RequiredPermission: Permission.None)
            { LabelKey = "settings.security" });

    internal static UiControl BuildContent(LayoutAreaHost host, StackControl stack, MeshNode? node)
    {
        var enabled = StepUpOptions.From(host.Hub.ServiceProvider.GetService<IConfiguration>()).Enabled;
        stack = stack
            .WithView(Controls.Title(host.Localize("settings.security"), 2))
            .WithView(Controls.Markdown(host.Localize("stepUp.settings.intro")))
            .WithView(Controls.Markdown(host.Localize("stepUp.settings.entra")));
        if (!enabled)
            stack = stack.WithView(Controls.Markdown(host.Localize("stepUp.settings.off")));
        return stack.WithView(Controls.Button(host.Localize("stepUp.enroll.manage"))
            .WithAppearance(Appearance.Accent)
            .WithClickAction(ctx =>
            {
                // An MVC endpoint: a client-side navigation would never reach it.
                ctx.NavigateTo(StepUpController.EnrollPath, forceLoad: true);
                return Task.CompletedTask;
            }));
    }
}
