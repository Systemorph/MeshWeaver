#pragma warning disable CS1591

using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Plugin.Packaging;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// The registry's ACQUISITION rules (#1664 step 13): who may hand a registry module bytes, and
/// which uploads it accepts. These are the decisions that cannot be walked back — bytes landed
/// under the wrong package id get served to that package's consumers, and a file name carrying a
/// path escapes <c>modules/&lt;name&gt;/</c> when written — so they are pinned here rather than
/// left to the route.
/// </summary>
public class ModulePublishTest
{
    private static BundleReader.Manifest Manifest(
        string? plugin = "SocialMedia",
        string? module = "MeshWeaver.Social",
        string? floor = "3.0.0",
        string? version = "1.2.0",
        IReadOnlyList<string>? assemblies = null) =>
        new(plugin, version, "sabc123",
            Assemblies: [],
            Module: new BundleReader.ModuleRef(module, assemblies ?? ["MeshWeaver.Social.dll"], floor));

    private static IReadOnlyList<BundleReader.ModuleFile> Files(params string[] names) =>
        [.. names.Select(n => new BundleReader.ModuleFile(n, [1, 2, 3]))];

    // ── authorization ─────────────────────────────────────────────────────────

    [Fact]
    public void NoTokenConfigured_RefusesEvenAValidLookingHeader()
    {
        // The route is not mapped in this state; if it ever is, "unconfigured" must never mean
        // "anyone may publish".
        Assert.NotNull(ModulePublish.DeclineAuthorization(null, "Bearer anything"));
        Assert.NotNull(ModulePublish.DeclineAuthorization("   ", "Bearer anything"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("secret")]                 // no scheme
    [InlineData("Basic secret")]           // wrong scheme
    [InlineData("Bearer wrong")]
    [InlineData("Bearer secret-with-suffix")]
    public void OnlyTheConfiguredBearerTokenMayPublish(string? header)
        => Assert.NotNull(ModulePublish.DeclineAuthorization("secret", header));

    [Theory]
    [InlineData("Bearer secret")]
    [InlineData("bearer secret")]          // the scheme is case-insensitive
    [InlineData("Bearer  secret  ")]       // surrounding whitespace is not part of the token
    public void TheConfiguredTokenIsAccepted(string header)
        => Assert.Null(ModulePublish.DeclineAuthorization("secret", header));

    // ── what may be landed ────────────────────────────────────────────────────

    [Fact]
    public void AWellFormedBundleIsAccepted_CarryingTheFloorAndVersionForward()
    {
        var (accepted, decline) = ModulePublish.Validate(
            "SocialMedia", Manifest(), Files("MeshWeaver.Social.dll", "MeshWeaver.Social.pdb"));

        Assert.Null(decline);
        Assert.NotNull(accepted);
        Assert.Equal("MeshWeaver.Social", accepted!.Module);
        Assert.Equal("1.2.0", accepted.Version);
        Assert.Equal("3.0.0", accepted.MinMeshVersion);   // re-checked at placement
        Assert.Equal("sabc123", accepted.FrameworkMvid);  // diagnostic only
        Assert.Equal(2, accepted.Files.Count);
    }

    [Fact]
    public void AnExplicitVersionOverridesTheManifests()
    {
        var (accepted, _) = ModulePublish.Validate(
            "SocialMedia", Manifest(version: "1.2.0"), Files("MeshWeaver.Social.dll"), version: "1.3.0");
        Assert.Equal("1.3.0", accepted!.Version);
    }

    /// <summary>
    /// 🚨 The check that matters most: bytes filed under the wrong package would be served to THAT
    /// package's consumers, who asked for something else entirely.
    /// </summary>
    [Fact]
    public void ABundleMayNotBePublishedUnderAnotherPackagesId()
    {
        var (accepted, decline) = ModulePublish.Validate("Approvals", Manifest(plugin: "SocialMedia"),
            Files("MeshWeaver.Social.dll"));
        Assert.Null(accepted);
        Assert.Contains("SocialMedia", decline);
        Assert.Contains("Approvals", decline);
    }

    [Fact]
    public void ABundleWithoutADeclaredPackageIsFiledWhereItWasPublished()
    {
        // Older bundles carry no plugin id. Refusing them would be a compatibility break for no
        // safety gain: the URL is the statement of intent, and the publisher is authenticated.
        var (accepted, decline) = ModulePublish.Validate(
            "SocialMedia", Manifest(plugin: null), Files("MeshWeaver.Social.dll"));
        Assert.Null(decline);
        Assert.Equal("MeshWeaver.Social", accepted!.Module);
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("nested/thing.dll")]
    [InlineData("nested\\thing.dll")]
    [InlineData("..")]
    [InlineData("")]
    public void AFileNameThatCouldEscapeTheModuleFolderIsRefused(string fileName)
    {
        var (accepted, decline) = ModulePublish.Validate(
            "SocialMedia", Manifest(), Files("MeshWeaver.Social.dll", fileName));
        Assert.Null(accepted);
        Assert.Contains("not a valid file name", decline);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("evil/../..")]
    [InlineData("with\\slash")]
    public void AModuleNameThatIsNotAFolderNameIsRefused(string module)
    {
        var (accepted, decline) = ModulePublish.Validate(
            "SocialMedia", Manifest(module: module), Files("MeshWeaver.Social.dll"));
        Assert.Null(accepted);
        Assert.Contains("not a valid module name", decline);
    }

    [Fact]
    public void ABundleWithoutItsEntryAssemblyIsRefused()
    {
        // modules/<name>/<name>.dll is what the loader resolves; a closure without it lands a
        // folder that can never load, and the failure would surface a restart later.
        var (accepted, decline) = ModulePublish.Validate(
            "SocialMedia", Manifest(), Files("SomethingElse.dll"));
        Assert.Null(accepted);
        Assert.Contains("entry assembly", decline);
    }

    [Fact]
    public void AnUploadWithNoModuleSectionIsRefused()
    {
        var (accepted, decline) = ModulePublish.Validate(
            "SocialMedia",
            new BundleReader.Manifest("SocialMedia", "1.0.0", "sabc", Assemblies: [], Module: null),
            Files("Whatever.dll"));
        Assert.Null(accepted);
        Assert.Contains("no module", decline);
    }

    [Fact]
    public void APackagePathStampsTheAcceptedUpload()
    {
        // The path is what stamps the landed entry's SOURCE — the key grants and the bundle
        // index match on. Its plugin half must agree with the URL, and traversal shapes are
        // refused before anything reaches disk.
        var manifest = Manifest(plugin: "SocialMedia", module: "MeshWeaver.Social");
        var files = Files("MeshWeaver.Social.dll");

        var (accepted, _) = ModulePublish.Validate(
            "SocialMedia", manifest, files, packagePath: "Plugins/SocialMedia");
        Assert.Equal("Plugins/SocialMedia", accepted!.PackagePath);

        // Absent → accepted unstamped (an older publisher), never refused.
        var (unstamped, _) = ModulePublish.Validate("SocialMedia", manifest, files);
        Assert.NotNull(unstamped);
        Assert.Null(unstamped!.PackagePath);

        Assert.NotNull(ModulePublish.Validate(
            "SocialMedia", manifest, files, packagePath: "Plugins/Other").DeclineReason);
        Assert.NotNull(ModulePublish.Validate(
            "SocialMedia", manifest, files, packagePath: "../SocialMedia").DeclineReason);
        Assert.NotNull(ModulePublish.Validate(
            "SocialMedia", manifest, files, packagePath: "Plugins/x/SocialMedia").DeclineReason);
    }

    [Fact]
    public void AnEmptyOrUnreadableUploadIsRefused()
    {
        Assert.NotNull(ModulePublish.Validate("SocialMedia", Manifest(), []).DeclineReason);
        Assert.NotNull(ModulePublish.Validate("SocialMedia", null, Files("x.dll")).DeclineReason);
        Assert.NotNull(ModulePublish.Validate("", Manifest(), Files("x.dll")).DeclineReason);
    }
}
