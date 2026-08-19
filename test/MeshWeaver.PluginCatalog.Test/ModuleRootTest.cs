#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using MeshWeaver.Mesh;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MeshWeaver.PluginCatalog.Test;

/// <summary>
/// <see cref="ModuleRoot"/> and the landed-root probe on <c>MeshBuilder.ResolveModulePath</c>.
///
/// <para>🚨 <b>Why this is worth pinning.</b> The writer (<see cref="ModuleLandingService"/>) and
/// boot-time activation must name ONE directory, and until #1664's publish route was first
/// exercised end to end they both took <c>AppContext.BaseDirectory</c> — which is READ-ONLY in the
/// container. Everything that only reads <c>/app/modules</c> worked, so the defect was invisible
/// until the first caller that had to WRITE it: the registry's publish route refused all fourteen
/// module bundles with <c>Access to the path '/app/modules/.staging-…' is denied</c>, surfaced to
/// the build as HTTP 409 — four steps from the cause.</para>
///
/// <para>The two rules below are the ones a future edit can silently break: the unconfigured
/// default must stay exactly what it was, and a landed module must be found under the configured
/// root (and must WIN over a same-named baseline, which is the whole point of landing one).</para>
/// </summary>
public class ModuleRootTest
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Unconfigured_keeps_the_app_directory(string? configured) =>
        // The default is load-bearing: every dev run, every test and every deployment that never
        // sets the key must behave exactly as it did before the key existed.
        Assert.Equal(AppContext.BaseDirectory, ModuleRoot.Resolve(configured));

    [Fact]
    public void A_configured_root_wins_and_is_trimmed() =>
        Assert.Equal("/data", ModuleRoot.Resolve("  /data  "));

    [Fact]
    public void The_root_is_read_from_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ModuleRoot.ConfigKey] = "/data",
            })
            .Build();

        Assert.Equal("/data", ModuleRoot.Resolve(configuration));
    }

    [Fact]
    public void A_null_configuration_is_not_an_error() =>
        Assert.Equal(AppContext.BaseDirectory, ModuleRoot.Resolve((IConfiguration?)null));

    [Fact]
    public void A_landed_module_is_found_under_the_configured_root_and_not_without_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "mw-moduleroot-" + Guid.NewGuid().ToString("N"));
        const string entry = "MeshWeaver.LandedOnly.Test.dll";
        var landed = Path.Combine(root, "modules", "MeshWeaver.LandedOnly.Test", entry);
        Directory.CreateDirectory(Path.GetDirectoryName(landed)!);
        File.WriteAllText(landed, "not a real assembly — only its PRESENCE is resolved here");

        try
        {
            Assert.Equal(landed, MeshBuilder.ResolveModulePath(entry, root));

            // Without the root the resolver cannot see it at all — it falls back to the app
            // closure, a path that does not exist. That gap IS the bug: landing wrote bytes
            // somewhere boot never looked.
            Assert.Equal(
                Path.Combine(AppContext.BaseDirectory, entry),
                MeshBuilder.ResolveModulePath(entry));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_landed_module_wins_over_a_same_named_baseline()
    {
        // A landed module is the one an operator just published, so a stale baseline copy of the
        // same name must not shadow it. Unique name + cleanup: this writes into the app directory,
        // which is shared with every other test in this assembly.
        var name = "MeshWeaver.Shadowed" + Guid.NewGuid().ToString("N")[..8] + ".Test";
        var entry = name + ".dll";

        var baselineDirectory = Path.Combine(AppContext.BaseDirectory, "modules", name);
        var root = Path.Combine(Path.GetTempPath(), "mw-moduleroot-" + Guid.NewGuid().ToString("N"));
        var landedDirectory = Path.Combine(root, "modules", name);

        Directory.CreateDirectory(baselineDirectory);
        Directory.CreateDirectory(landedDirectory);
        var baseline = Path.Combine(baselineDirectory, entry);
        var landed = Path.Combine(landedDirectory, entry);
        File.WriteAllText(baseline, "baseline");
        File.WriteAllText(landed, "landed");

        try
        {
            Assert.Equal(landed, MeshBuilder.ResolveModulePath(entry, root));
            // …and with no root configured the baseline is still resolved, unchanged.
            Assert.Equal(baseline, MeshBuilder.ResolveModulePath(entry));
        }
        finally
        {
            Directory.Delete(baselineDirectory, recursive: true);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_rooted_entry_passes_through_untouched()
    {
        var rooted = Path.Combine(Path.GetTempPath(), "explicit", "Some.Module.dll");
        Assert.Equal(rooted, MeshBuilder.ResolveModulePath(rooted, "/data"));
    }
}
