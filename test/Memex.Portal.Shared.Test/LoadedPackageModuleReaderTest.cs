using System.Collections.Generic;
using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 MeshWeaver#6067 follow-up: the dependency floor a GitSync import checks is judged against the
/// module generation this process LOADED, never against the newest LANDED one — a landing is
/// restart-as-activation, so the head can name 1.21.0 while 1.20.4 is what runs.
/// </summary>
public class LoadedPackageModuleReaderTest
{
    private static ModuleActivationEntry Ai(string? previousDirectory = "MeshWeaver.AI@1204", bool enabled = true) => new()
    {
        Name = "MeshWeaver.AI",
        PackagePath = "Plugins/AI",
        Directory = "MeshWeaver.AI@1210",
        Version = "1.21.0",
        PreviousDirectory = previousDirectory,
        PreviousVersion = "1.20.4",
        Enabled = enabled,
    };

    private static ModuleActivationList List(params ModuleActivationEntry[] entries) => new() { Entries = [.. entries] };

    [Fact]
    public void TheHeadLoaded_ReadsTheHeadsVersion()
        => LoadedPackageModuleReader.Resolve(List(Ai()),
                new Dictionary<string, string> { ["MeshWeaver.AI"] = "MeshWeaver.AI@1210" })["AI"]
            .Version.Should().Be("1.21.0");

    /// <summary>THE case: 1.21.0 landed, the process still runs the 1.20.4 generation it booted on.</summary>
    [Fact]
    public void ALandingPendingRestart_ReadsTheVersionThatRuns()
        => LoadedPackageModuleReader.Resolve(List(Ai()),
                new Dictionary<string, string> { ["MeshWeaver.AI"] = "MeshWeaver.AI@1204" })["ai"]
            .Version.Should().Be("1.20.4", "the floor is judged against what LOADED, and the key is case-insensitive");

    [Theory]
    [InlineData("MeshWeaver.AI")]      // the image's copy: no landing labelled it
    [InlineData("MeshWeaver.AI@9999")] // a generation neither the head nor the previous names
    public void AGenerationNoEntryLabels_IsNotJudged(string leaf)
        => LoadedPackageModuleReader.Resolve(List(Ai()),
                new Dictionary<string, string> { ["MeshWeaver.AI"] = leaf })
            .Should().BeEmpty("absent means not judged — never a guessed version");

    [Fact]
    public void AnUnloadedOrUninstalledModule_IsNotJudged()
    {
        LoadedPackageModuleReader.Resolve(List(Ai()), new Dictionary<string, string>()).Should().BeEmpty();
        LoadedPackageModuleReader.Resolve(List(Ai(enabled: false)),
                new Dictionary<string, string> { ["MeshWeaver.AI"] = "MeshWeaver.AI@1210" })
            .Should().BeEmpty();
    }
}
