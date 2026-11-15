using System;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.PluginTester.Test;

/// <summary>
/// Pins <c>MW_LOG_CATEGORIES</c> (MeshWeaver#4413). At the gate's default Warning level a named
/// category used to be filtered AT Warning — naming it raised nothing — so the switch only worked
/// under <c>MW_LOG_LEVEL=Trace</c>, which turns the whole run chatty. The combo lane names the
/// adoption categories to learn why the gate recompiles what the bake compiled.
/// </summary>
public class GateVerbosityCategoryFiltersTest
{
    /// <summary>The regression: at the default Warning, a bare category is raised to Information
    /// (the old code returned Warning here, i.e. no raise at all).</summary>
    [Fact]
    public void ABareCategory_IsRaisedToInformation_AtTheDefaultLevel()
        => Assert.Equal(
            new[] { ("bake-seed", LogLevel.Information) },
            GateVerbosity.CategoryFilters("bake-seed", LogLevel.Warning));

    /// <summary>An entry's own <c>=Level</c> is honoured case-insensitively, and whitespace and
    /// empty entries are ignored.</summary>
    [Fact]
    public void ANamedLevel_IsHonoured_AndEntriesAreTrimmed()
        => Assert.Equal(
            new[]
            {
                ("bake-seed", LogLevel.Debug),
                ("MeshWeaver.Graph.CompileWatcher", LogLevel.Debug),
                ("Other", LogLevel.Information),
            },
            GateVerbosity.CategoryFilters(
                " bake-seed=debug , MeshWeaver.Graph.CompileWatcher=Debug,Other,", LogLevel.Warning));

    /// <summary>A category is never made LESS verbose than the run itself: under the /debug
    /// skill's MW_LOG_LEVEL=Trace a named category stays at Trace.</summary>
    [Fact]
    public void ACategory_IsNeverQuieterThanTheRun()
        => Assert.Equal(
            new[] { ("A", LogLevel.Trace), ("B", LogLevel.Trace) },
            GateVerbosity.CategoryFilters("A,B=Warning", LogLevel.Trace));

    /// <summary>An unset or empty variable adds no filter.</summary>
    [Fact]
    public void UnsetOrEmpty_AddsNothing()
    {
        Assert.Empty(GateVerbosity.CategoryFilters(null, LogLevel.Warning));
        Assert.Empty(GateVerbosity.CategoryFilters(" , ", LogLevel.Warning));
    }

    /// <summary>A typo'd level is refused by name rather than silently read as Information — and
    /// so is a NUMERIC one, which <c>Enum.TryParse</c> alone would accept.</summary>
    [Theory]
    [InlineData("bake-seed=Verbose")]
    [InlineData("bake-seed=2")]
    public void AnUnknownOrNumericLevel_IsRefused(string raw)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => GateVerbosity.CategoryFilters(raw, LogLevel.Warning));
        Assert.Contains(raw, ex.Message);
    }
}
