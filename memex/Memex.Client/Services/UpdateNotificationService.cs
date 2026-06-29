using System.Reactive.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using NuGet.Versioning;
using Microsoft.Extensions.Logging;

namespace Memex.Client.Services;

/// <summary>
/// Detect-and-notify for the native client. A sandboxed app can't replace its own binary, so when a
/// connected remote mesh runs a NEWER platform version than this app's bundled one, we surface an
/// in-app alert telling the user to update the app (store / TestFlight) and relaunch — the MAUI half
/// of the platform self-update strategy (Doc/Architecture/ReleaseStrategy). The portal half (in-pod
/// patch) does not apply here.
/// </summary>
public sealed partial class UpdateNotificationService(ILogger<UpdateNotificationService>? logger = null)
{
    /// <summary>The platform version baked into this app's bundled MeshWeaver assemblies (the same
    /// central <c>PlatformVersion</c> from Directory.Build.props, via InformationalVersion).</summary>
    public static string LocalPlatformVersion
    {
        get
        {
            var asm = Assembly.GetEntryAssembly() ?? typeof(MeshNode).Assembly;
            return asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? asm.GetName().Version?.ToString()
                ?? "unknown";
        }
    }

    /// <summary>
    /// Best-effort check after connecting to a remote mesh <paramref name="meshId"/>: reads the
    /// remote's <c>Admin/PlatformVersion</c> node (via a query — empty-on-absent, never a point-read
    /// that could NotFound-storm), and if its version is newer than <see cref="LocalPlatformVersion"/>
    /// shows a one-time alert. Any failure (unroutable, absent, timeout) just logs — never throws.
    /// </summary>
    public void CheckRemote(IWorkspace workspace, string meshId)
    {
        var remotePath = $"{meshId}/Admin/PlatformVersion";
        workspace.GetQuery($"update-check-{meshId}", $"path:{remotePath} nodeType:Markdown")
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(20))
            // The platform-version node body carries the version in bold (**X**). Read it off the
            // raw content string so this works whether Content is a typed record or a query-row
            // JsonElement — and without coupling the client to the Markdown content type.
            .Select(nodes => ParsePlatformVersion(nodes.FirstOrDefault()?.Content?.ToString()))
            .Where(remote => remote is not null && IsNewer(remote!, LocalPlatformVersion))
            .Subscribe(
                remote => Notify(meshId, remote!),
                ex => logger?.LogDebug(ex, "Update check skipped for {MeshId}", meshId));
    }

    /// <summary>Extracts the version from the platform-version node body
    /// (<c>Installed platform version: **X**</c>); null when not found.</summary>
    public static string? ParsePlatformVersion(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return null;
        var m = VersionInBold().Match(markdown);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    /// <summary>True if <paramref name="remote"/> is a strictly newer platform version than
    /// <paramref name="local"/> (SemVer2). Unparseable local version → false (never nag on an
    /// unstamped build).</summary>
    public static bool IsNewer(string remote, string local) =>
        NuGetVersion.TryParse(remote, out var r)
        && NuGetVersion.TryParse(local, out var l)
        && r > l;

    private void Notify(string meshId, string remoteVersion)
    {
        logger?.LogInformation(
            "Remote {MeshId} runs platform {Remote}; this app is {Local} — prompting to update.",
            meshId, remoteVersion, LocalPlatformVersion);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var page = Application.Current?.Windows.FirstOrDefault()?.Page;
            if (page is null)
                return;
            _ = page.DisplayAlert(
                "Update available",
                $"{meshId} is running a newer version ({remoteVersion}). Update this app from the store " +
                "and relaunch to get the latest features.",
                "OK");
        });
    }

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex VersionInBold();
}
