using MeshWeaver.Deployment;

namespace Aspire.Hosting;

/// <summary>
/// The Aspire adapter's only inputs that are NOT part of the Deployment record: where the images
/// come from when the record names no repository, and which version to start on. Everything else
/// an instance is — host, database, plugins, modules, volumes, sign-in, email, AI, operator — lives
/// on the <see cref="DeploymentContent"/> record the AppHost builds fluently
/// (<see cref="MemexHostingExtensions.AddMemex(IDistributedApplicationBuilder, string, Func{DeploymentContent, DeploymentContent}?)"/>),
/// which is the ONE input Helm renders from too. This type used to be a second, adapter-only copy
/// of half of those fields; that copy is gone by design (maintainer, 2026-09-08: "the record must
/// be the only input").
/// </summary>
public static class MemexOptions
{
    /// <summary>Container registry + namespace the published Memex images live under. Default GHCR / Systemorph.</summary>
    public const string DefaultImageRegistry = "ghcr.io/systemorph";

    /// <summary>The portal image with the co-hosted Claude Code + GitHub Copilot CLIs baked in (the default).</summary>
    public const string DefaultPortalRepository = "memex-portal-ai";

    /// <summary>
    /// <c>&lt;major&gt;-latest</c> for the major of the assembly this adapter ships in. Derived,
    /// not typed, so a 4.x adapter cannot be published still naming <c>3-latest</c>. CD moves the
    /// pointer to every sealed set of that major and never to another major; the portal starts on
    /// it and its own self-updater takes over from there, so the package version and the image
    /// version are independent. The <c>latest</c> fallback exists only for an unstamped local build.
    /// </summary>
    public static string DefaultImageTag { get; } =
        typeof(MemexOptions).Assembly.GetName().Version is { Major: > 0 } v ? $"{v.Major}-latest" : "latest";

    /// <summary>
    /// The record a bare <c>AddMemex(name)</c> starts from: the default images on the default
    /// registry, one replica, and the fleet's three volumes at the record's default storage
    /// layout paths — <c>/data</c> (the state root: DataProtection keys, the module cache, the
    /// NuGet cache), <c>/mnt/content</c> (the content collection) and <c>/mnt/users</c> (the
    /// per-user co-hosted-CLI config). Every other field is the record's own default — the
    /// builder adds nothing (rule (c) of the fluent surface).
    /// </summary>
    public static DeploymentContent DefaultRecord(string name) =>
        new DeploymentContent { Namespace = name, HelmRelease = name }
            .WithImage($"{DefaultImageRegistry}/{DefaultPortalRepository}", tag: DefaultImageTag)
            .WithReplicas(1)
            .WithVolume("data", "/data")
            .WithVolume("content", "/mnt/content")
            .WithVolume("users", "/mnt/users");
}
