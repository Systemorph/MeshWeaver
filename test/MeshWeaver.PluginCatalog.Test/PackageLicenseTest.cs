#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// A package's licence: declared on the plugin root, or filled from the SOURCE's default.
///
/// <para>🚨 The boundary under test is that the fallback is SOURCE-scoped and never platform-wide.
/// A fallback records a grant the copyright holder already made (a repo's own LICENSE file); a
/// platform-wide default would assert a licence a third-party author never gave. So a source with
/// no declared default must leave the licence UNSPECIFIED rather than guessing — "we don't know"
/// is the honest answer, and it is what lets a UI ask before installing.</para>
/// </summary>
public class PackageLicenseTest
{
    private static readonly IReadOnlyList<RepoFile> Repo =
    [
        // Declares its own licence — must win over any source default.
        new("Declared/index.json",
            """{"$type":"MeshNode","id":"Declared","namespace":"","path":"Declared","mainNode":"Declared","name":"Declared","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"x","license":"Apache-2.0"}}"""),
        // Declares nothing — inherits the source default, when there is one.
        new("Silent/index.json",
            """{"$type":"MeshNode","id":"Silent","namespace":"","path":"Silent","mainNode":"Silent","name":"Silent","nodeType":"Space","state":"Active","content":{"$type":"PluginManifest","description":"x"}}"""),
    ];

    private static NodeRepoPackageSource Source(string? defaultLicense)
    {
        Func<string, string, string?, string, IObservable<RepoSnapshot>> fetch =
            (_, _, _, _) => Observable.Return(new RepoSnapshot("commit-lic", Repo));
        return new NodeRepoPackageSource(
            fetch, "https://github.com/acme/plugins", token: "", logger: null,
            defaultLicense: defaultLicense);
    }

    [Fact(Timeout = 60_000)]
    public async Task DeclaredLicenseIsRead_AndWinsOverTheSourceDefault()
    {
        var packages = await Source("MIT").ListPackages("HEAD").Should().Emit();
        packages.Single(p => p.Id == "Declared").License.Should().Be("Apache-2.0");
    }

    [Fact(Timeout = 60_000)]
    public async Task UndeclaredInheritsTheSourceDefault()
    {
        // The first-party case: the repo's LICENSE is MIT, so a package omitting the field
        // genuinely IS MIT — the fallback records that, it does not invent it.
        var packages = await Source(WellKnownLicenses.FirstPartyFallback).ListPackages("HEAD").Should().Emit();
        packages.Single(p => p.Id == "Silent").License.Should().Be("MIT");
    }

    [Fact(Timeout = 60_000)]
    public async Task NoSourceDefault_LeavesItUnspecified_NeverGuessed()
    {
        // The third-party case. Defaulting here would assert a grant nobody made.
        var packages = await Source(defaultLicense: null).ListPackages("HEAD").Should().Emit();
        packages.Single(p => p.Id == "Silent").License.Should().BeNull();
        packages.Single(p => p.Id == "Declared").License.Should().Be("Apache-2.0");
    }

    [Fact]
    public void ThePlatformOffersApacheFirst()
    {
        // Apache-2.0 is named first because it is the preferred licence and the one new
        // contributions are made under (its section 5 covers inbound contributions; MIT is silent).
        WellKnownLicenses.PlatformSpdxExpression.Should().Be("Apache-2.0 OR MIT");
        WellKnownLicenses.Shipped.First().Should().Be("Apache-2.0");
    }
}
