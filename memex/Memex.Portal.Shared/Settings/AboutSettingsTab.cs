using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace Memex.Portal.Shared.Settings;

/// <summary>
/// Global settings "About" tab — shows EXACTLY which build is running (platform version + the git
/// commit it was built from) with a link straight to the GitHub commit, plus the repository and
/// runtime. Ungated: visible to every user. Build identity comes from the assembly attributes baked
/// in by <c>Directory.Build.props</c> (<see cref="ShippedReleaseSeed.InstalledPlatformVersion"/> /
/// <see cref="ShippedReleaseSeed.CommitHash"/>), so it reflects the deployed image with no runtime
/// plumbing. Pure <see cref="Controls"/> + markdown — no hand-built HTML (GUI rule).
/// </summary>
public static class AboutSettingsTab
{
    /// <summary>The tab id under <c>/_settings/GlobalSettings</c>.</summary>
    public const string TabId = "About";

    /// <summary>Registers the About tab with the global settings menu (ungated).</summary>
    public static MessageHubConfiguration AddAboutSettingsTab(this MessageHubConfiguration config)
        => config.AddGlobalSettingsMenuItems(new GlobalSettingsMenuItemProvider(GetTab));

    private static IObservable<IReadOnlyList<GlobalSettingsMenuItemDefinition>> GetTab(
        LayoutAreaHost host, RenderingContext ctx)
        => Observable.Return<IReadOnlyList<GlobalSettingsMenuItemDefinition>>(new[]
        {
            new GlobalSettingsMenuItemDefinition(
                Id: TabId,
                Label: "About",
                ContentBuilder: BuildContent,
                Icon: FluentIcons.Info(),
                Order: 900)
        });

    internal static UiControl BuildContent(LayoutAreaHost host, StackControl stack)
    {
        stack = stack.WithView(Controls.H2("About MeshWeaver").WithStyle("margin: 0 0 8px 0;"));
        stack = stack.WithView(Controls.Markdown(
            "MeshWeaver is a reactive, mesh-based data and automation platform. This page identifies " +
            "the exact build this portal is running."));

        stack = stack.WithView(Controls.Markdown(BuildInfoMarkdown()));
        return stack;
    }

    /// <summary>
    /// The build-identity block. Version is always present; the commit line links to the exact
    /// GitHub commit when the SHA was baked in (CI + local git), otherwise it is omitted with a note.
    /// </summary>
    private static string BuildInfoMarkdown()
    {
        var version = ShippedReleaseSeed.InstalledPlatformVersion;
        var sha = ShippedReleaseSeed.CommitHash;
        var commitLine = sha is { Length: > 0 }
            ? $"**Build commit:** [`{Short(sha)}`]({ShippedReleaseSeed.CommitUrl})"
            : "**Build commit:** _not recorded for this build_";

        return
            $"**Version:** `{version}`\n\n" +
            $"{commitLine}\n\n" +
            $"**Repository:** [Systemorph/MeshWeaver]({ShippedReleaseSeed.RepositoryUrl})\n\n" +
            $"**Runtime:** .NET {Environment.Version}";
    }

    /// <summary>Short display form of a commit SHA (first 12 chars); full SHA still drives the link.</summary>
    private static string Short(string sha) => sha.Length <= 12 ? sha : sha[..12];
}
