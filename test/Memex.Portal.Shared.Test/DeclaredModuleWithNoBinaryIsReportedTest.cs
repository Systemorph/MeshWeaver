using System;
using System.Collections.Generic;
using System.Linq;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A MIXED PACKAGE WHOSE MODULE NEVER LANDS MUST SAY SO — never report a clean install
/// (Systemorph/MeshWeaver.Plugins#1597).</b>
///
/// <para>A package that declares <c>content.module</c> arrives in two halves: its NODES come with
/// the package, its layout areas and node types come only with the compiled module. Installing the
/// first half and not the second is a SILENT and TOTAL loss of the second, and every surface reads
/// clean: the package is <c>preInstalled</c>, its install record names the module, the boot
/// reconcile reports it up to date, its guide page renders, and only the areas are absent.</para>
///
/// <para>Measured on a <c>memex-local</c> self-registry install, platform 3.0.0-ci.8118: the
/// <c>Export</c> package was installed and <c>Export/Guide</c> rendered, while
/// <c>get @Export/Guide/area/ExportPdf</c> answered <i>Area not found</i> and the node menu carried
/// no Export group at all. <c>MeshWeaver.Markdown.Export.dll</c> was in neither <c>/app</c> nor
/// <c>/app/modules</c>, and a mounted checkout can never land a module BINARY
/// (Systemorph/MeshWeaver#2417). Walking a course to completion issues a correct certificate that
/// cannot then be exported — and nothing anywhere says why.</para>
///
/// <para><see cref="ModuleDelivery.NotOffered"/> does not cover this and cannot: it answers whether
/// the REGISTRY offered the package, and a self-registry install offers its own checkout perfectly
/// well. The unreported state is the next one along — offered, installed, and the binary still has
/// no lane that can produce it here.</para>
///
/// <para>🚨 This REPORTS. It never refuses, and it is not an entitlement verdict — the same
/// discipline <see cref="ModuleLoadReport"/> keeps, and for the same reason: a portal that will not
/// boot cannot be given the fix for whatever is wrong with it. Which modules a deployment SHOULD
/// carry is a policy question this does not answer; making the shortfall visible is the whole
/// deliverable.</para>
/// </summary>
public class DeclaredModuleWithNoBinaryIsReportedTest
{
    private static PackageManifest Package(string id, string? module, string? version = null) =>
        new() { Id = id, Name = id, Module = module, ModuleVersion = version };

    /// <summary>Present exactly for the modules named here; absent for everything else.</summary>
    private static Func<string, bool> Present(params string[] modules)
    {
        var set = modules.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return set.Contains;
    }

    [Fact]
    public void ADeclaredModuleWithNoBinaryOnThisInstallationIsNamed()
    {
        var absent = ModuleDelivery.BinaryAbsent(
            [Package("Export", "MeshWeaver.Markdown.Export", "1.3")],
            Present());

        var only = Assert.Single(absent);
        Assert.Equal("Export", only.PackageId);
        Assert.Equal("MeshWeaver.Markdown.Export", only.Module);
        // The claimed identity rides along, because that is what makes the shape legible: the
        // record says 1.3 while there is no assembly at all.
        Assert.Equal("1.3", only.InstalledModuleVersion);
        Assert.Contains("MeshWeaver.Markdown.Export", only.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void AModuleWhoseBinaryIsHereIsNotNamed()
    {
        Assert.Empty(ModuleDelivery.BinaryAbsent(
            [Package("Mcp", "MeshWeaver.Mcp")],
            Present("MeshWeaver.Mcp")));
    }

    /// <summary>
    /// 🚨 The negative that keeps this usable. Most installed packages are content-only, and naming
    /// them would bury the ones that have actually lost half of themselves — the annotation-on-every
    /// -green-run failure this repo has met before.
    /// </summary>
    [Fact]
    public void AContentOnlyPackageIsNeverNamed_AndIsNeverEvenProbed()
    {
        var probed = new List<string>();
        var absent = ModuleDelivery.BinaryAbsent(
            [Package("Northwind", null), Package("Chess", "   ")],
            module => { probed.Add(module); return false; });

        Assert.Empty(absent);
        // Probing the filesystem for a module nobody declared would put a name in the log that no
        // install record ever claimed.
        Assert.Empty(probed);
    }

    [Fact]
    public void EveryDeclaredModuleIsProbedExactlyOnce()
    {
        var probed = new List<string>();
        ModuleDelivery.BinaryAbsent(
            [Package("Export", "MeshWeaver.Markdown.Export"), Package("Voice", "MeshWeaver.Speech")],
            module => { probed.Add(module); return false; });

        Assert.Equal(["MeshWeaver.Markdown.Export", "MeshWeaver.Speech"], probed.Order());
        Assert.Equal(2, probed.Count);
    }

    /// <summary>Ordered and case-insensitive on the package id — the same comparison every other
    /// install-record lookup on this path uses, so two readings never disagree on order.</summary>
    [Fact]
    public void TheReportIsOrderedByPackageId()
    {
        var absent = ModuleDelivery.BinaryAbsent(
            [Package("voice", "MeshWeaver.Speech"), Package("Export", "MeshWeaver.Markdown.Export")],
            Present());

        Assert.Equal(["Export", "voice"], absent.Select(u => u.PackageId));
    }

    /// <summary>An unreadable record's content arrives as null, and a record with no id is not a
    /// record. Neither may take down a boot-time report.</summary>
    [Fact]
    public void NullsAndIdlessRecordsAreSkipped_AndNullInputIsEmpty()
    {
        Assert.Empty(ModuleDelivery.BinaryAbsent(null, Present()));
        Assert.Empty(ModuleDelivery.BinaryAbsent(
            [null, Package("", "MeshWeaver.Markdown.Export")], Present()));
    }

    /// <summary>🚨 A probe that cannot be consulted must not silently answer "everything is fine".
    /// No probe means nothing is known, and the report says nothing rather than clearing every
    /// package.</summary>
    [Fact]
    public void WithNoProbeNothingIsClaimed()
    {
        Assert.Empty(ModuleDelivery.BinaryAbsent(
            [Package("Export", "MeshWeaver.Markdown.Export")], null));
    }
}
