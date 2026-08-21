#pragma warning disable CS1591

using System;
using System.IO;
using MeshWeaver.Hosting.AspNetCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// A module's scoped-CSS aggregate opens with <c>@import</c> lines for its DEPENDENCIES' bundles at
/// fingerprints computed by the MODULE's build. For a dependency the host also provides, the host
/// serves its own copy under ITS fingerprint — so the import can never resolve, and 404s once per
/// page load with nothing in any log. They are redundant too: the host links its own aggregate,
/// which already contains those bundles.
///
/// <para>The shape is real, measured from a standalone publish of MeshWeaver.Blazor.Analysis.</para>
/// </summary>
public class ModuleStylesheetImportTest
{
    private const string RealAggregate = """
        @import '_content/MeshWeaver.Blazor/MeshWeaver.Blazor.4p29wy9ysg.bundle.scp.css';
        @import '_content/Microsoft.FluentUI.AspNetCore.Components/Microsoft.FluentUI.AspNetCore.Components.lcdo7z9xd2.bundle.scp.css';

        /* _content/MeshWeaver.Blazor.Analysis/KpiStripView.razor.rz.scp.css */
        .kpi-strip[b-jkho40fn1g] {
            display: flex;
        }
        """;

    /// <summary>A web root that claims to provide exactly the named dependencies.</summary>
    private static IFileProvider HostProviding(params string[] dependencies)
    {
        var root = Path.Combine(Path.GetTempPath(), "mw-hostassets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);   // PhysicalFileProvider requires an existing root
        foreach (var dependency in dependencies)
            Directory.CreateDirectory(Path.Combine(root, "_content", dependency));
        return new PhysicalFileProvider(root);
    }

    [Fact]
    public void AnImportOfAHostProvidedDependency_IsStripped()
    {
        var rewritten = MeshModuleStaticAssetExtensions.StripHostProvidedImports(
            RealAggregate,
            HostProviding("MeshWeaver.Blazor", "Microsoft.FluentUI.AspNetCore.Components"),
            "MeshWeaver.Blazor.Analysis",
            NullLogger.Instance);

        Assert.NotNull(rewritten);
        Assert.DoesNotContain("@import", rewritten);
        // The module's OWN rules survive untouched — including the scope id, which is what makes
        // them match its components' attributes.
        Assert.Contains("b-jkho40fn1g", rewritten);
        Assert.Contains(".kpi-strip", rewritten);
    }

    [Fact]
    public void AnImportTheHostDoesNOTProvide_IsKept()
    {
        // The module's own wwwroot/_content/<Dep>/ carries that bundle at exactly the fingerprint
        // the import names — both came from the same publish, so the URL resolves.
        var rewritten = MeshModuleStaticAssetExtensions.StripHostProvidedImports(
            RealAggregate,
            HostProviding("MeshWeaver.Blazor"),
            "MeshWeaver.Blazor.Analysis",
            NullLogger.Instance);

        Assert.NotNull(rewritten);
        Assert.Contains("Microsoft.FluentUI.AspNetCore.Components.lcdo7z9xd2.bundle.scp.css", rewritten);
        Assert.DoesNotContain("MeshWeaver.Blazor.4p29wy9ysg", rewritten);
    }

    [Fact]
    public void NothingToStrip_ReturnsNull_SoTheFileIsServedUntouched()
    {
        // Null is the signal that the ordinary static-file path keeps serving the landed bytes —
        // a generation is immutable, and an unnecessary in-memory copy would be a second truth.
        Assert.Null(MeshModuleStaticAssetExtensions.StripHostProvidedImports(
            RealAggregate, HostProviding(), "MeshWeaver.Blazor.Analysis", NullLogger.Instance));

        Assert.Null(MeshModuleStaticAssetExtensions.StripHostProvidedImports(
            ".x[b-abc] { color: red; }", HostProviding("MeshWeaver.Blazor"),
            "MeshWeaver.Blazor.Analysis", NullLogger.Instance));
    }

    /// <summary>
    /// A pack with collocated JS but NO <c>.razor.css</c> of its own still emits an aggregate —
    /// one made of nothing but the dependency imports. Measured at 211 bytes for both
    /// MeshWeaver.Blazor.AppleMaps and .OpenStreetMap on the publish that flipped them (#1974).
    /// Stripping empties it, which is why the caller must decide whether to link the stylesheet
    /// AFTER stripping: the file on disk is never empty, so a test on its bytes never fires.
    /// </summary>
    [Fact]
    public void AnAggregateOfNothingButHostProvidedImports_StripsToEmpty()
    {
        const string importsOnly =
            "@import '_content/MeshWeaver.Blazor/MeshWeaver.Blazor.vbmbh1xu9q.bundle.scp.css';\n"
            + "@import '_content/Microsoft.FluentUI.AspNetCore.Components/"
            + "Microsoft.FluentUI.AspNetCore.Components.lcdo7z9xd2.bundle.scp.css';\n";

        var rewritten = MeshModuleStaticAssetExtensions.StripHostProvidedImports(
            importsOnly,
            HostProviding("MeshWeaver.Blazor", "Microsoft.FluentUI.AspNetCore.Components"),
            "MeshWeaver.Blazor.AppleMaps",
            NullLogger.Instance);

        // Not null — something WAS dropped, so the landed bytes must not be served as they are.
        Assert.NotNull(rewritten);
        Assert.True(string.IsNullOrWhiteSpace(rewritten));
    }
}
