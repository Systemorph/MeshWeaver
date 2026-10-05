#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using MeshWeaver.Compiler;
using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A prebuilt's module dependency must MOVE with the module</b>
/// (<c>Doc/Architecture/DependencyRecordFloor</c>, section "A floor must move with the module").
///
/// <para><b>The incident (2026-10-05).</b> The control instance ran store package AI 1.20.4. The
/// Hosting prebuilt it adopted had been compiled against an AI build that added
/// <c>ThreadPreparation.Group</c>, and recorded its AI dependency as <c>min:3.0.0.0</c> — the
/// module's informational version, which every AI build inherits from the platform. A floor that
/// never moves is satisfied by every build, so the prebuilt was ADOPTED and failed at call time
/// with <c>MissingMethodException: set_Group</c>; the PR reviewer could not start.</para>
///
/// <para>The fixtures are REAL assemblies: two emitted builds of one module name that state the
/// SAME informational version (exactly as two AI builds do) and DIFFERENT package versions. The
/// control arm (<see cref="Control_TheInformationalFloorCannotTellTheTwoBuildsApart"/>) reproduces
/// the defect through the pre-fix resolver; the arms after it pin the decline, the floor's
/// relaxation upward, and the transition (an unstamped installed module never satisfies a
/// package floor).</para>
/// </summary>
public class PrebuiltBindsModulePackageVersionTest : IDisposable
{
    private const string Module = "MeshWeaver.AI";
    private const string PlatformStamp = "3.0.0.0";
    private const string Key = "c003e001";
    private const string Toolchain = "compat:c003e001";

    private readonly string root = Path.Combine(Path.GetTempPath(), $"mw-pkgver-{Guid.NewGuid():N}");
    private readonly List<AssemblyLoadContext> contexts = [];

    public void Dispose()
    {
        foreach (var context in contexts)
            context.Unload();
        GC.SuppressFinalize(this);
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory costs disk, never correctness.
        }
    }

    [Fact]
    public void ThePackageVersionIsReadFromTheModulesOwnMetadata()
    {
        var built = Load("built", packageVersion: "1.21.0");
        var unstamped = Load("unstamped", packageVersion: null);

        Assert.Equal("1.21.0", InstalledModuleAssembly.PackageVersionOf(built));
        Assert.Equal("1.21.0", new InstalledModuleAssembly(built).PackageVersion);
        Assert.Null(InstalledModuleAssembly.PackageVersionOf(unstamped));
        // Both builds state the platform's informational version — the reason min: could not work.
        Assert.Equal(PlatformStamp, InstalledModuleAssembly.VersionOf(built));
        Assert.Equal(PlatformStamp, InstalledModuleAssembly.VersionOf(unstamped));
    }

    /// <summary>The defect, reproduced: through the resolver as it was (no package version), the
    /// record a 1.21.0 build produces is satisfied by the installed 1.20.4.</summary>
    [Fact]
    public void Control_TheInformationalFloorCannotTellTheTwoBuildsApart()
    {
        var producer = Load("producer", packageVersion: "1.21.0");
        var installed = Load("installed", packageVersion: "1.20.4");

        var record = CompiledDependencies.Compute([Module], InformationalOnly(producer), Toolchain);
        Assert.Equal("min:" + PlatformStamp, record[Module]);
        Assert.Null(CompiledDependencies.FindMismatch(record, InformationalOnly(installed), Toolchain));
    }

    [Fact]
    public void APrebuiltBoundToANewerModuleThanTheInstalledOne_IsDeclined_NamingBothVersions()
    {
        var producer = Load("producer", packageVersion: "1.21.0");
        var installed = Load("installed", packageVersion: "1.20.4");

        var record = CompiledDependencies.Compute([Module], ResolverOver(producer), Toolchain);
        Assert.Equal("pkg:1.21.0", record[Module]);

        var mismatch = CompiledDependencies.FindMismatch(record, ResolverOver(installed), Toolchain);
        Assert.NotNull(mismatch);
        Assert.Contains("1.21.0", mismatch, StringComparison.Ordinal);
        Assert.Contains("1.20.4", mismatch, StringComparison.Ordinal);
        Assert.Equal(DependencyRecordStatus.FloorNotMet,
            CompiledDependencies.Validate(record, ResolverOver(installed), Toolchain).Status);
    }

    [Fact]
    public void TheSameOrANewerInstalledModule_StillAdopts()
    {
        var producer = Load("producer", packageVersion: "1.21.0");
        var record = CompiledDependencies.Compute([Module], ResolverOver(producer), Toolchain);

        Assert.Null(CompiledDependencies.FindMismatch(
            record, ResolverOver(Load("same", packageVersion: "1.21.0")), Toolchain));
        Assert.Null(CompiledDependencies.FindMismatch(
            record, ResolverOver(Load("newer", packageVersion: "1.22.3")), Toolchain));
    }

    /// <summary>🚨 The transition: an installed module built before the stamp existed resolves
    /// <c>min:3.0.0.0</c>. It must NOT satisfy <c>pkg:1.21.0</c> (3.0.0.0 ≥ 1.21.0 would be the
    /// incident again) — across schemes nothing is checked and the prebuilt is declined.</summary>
    [Fact]
    public void AnUnstampedInstalledModule_NeverSatisfiesAPackageFloor()
    {
        var producer = Load("producer", packageVersion: "1.21.0");
        var record = CompiledDependencies.Compute([Module], ResolverOver(producer), Toolchain);

        var legacy = ResolverOver(Load("legacy", packageVersion: null));
        Assert.Equal("min:" + PlatformStamp, legacy(Module));
        Assert.NotNull(CompiledDependencies.FindMismatch(record, legacy, Toolchain));
        Assert.Equal(DependencyRecordStatus.NotChecked,
            CompiledDependencies.Validate(record, legacy, Toolchain).Status);
    }

    [Fact]
    public void ThePackageIdIsAModuleLaneId()
    {
        Assert.True(CompiledDependencies.IsModuleLaneId("pkg:1.21.0"));
        Assert.Equal("pkg:1.21.0", CompiledDependencies.ModuleIdOf("abc", "1.21.0", PlatformStamp));
        Assert.Equal("min:" + PlatformStamp, CompiledDependencies.ModuleIdOf("abc", null, PlatformStamp));
        Assert.Equal("mvid:abc", CompiledDependencies.ModuleIdOf("abc", null, null));
    }

    private static Func<string, string?> ResolverOver(Assembly module)
    {
        var installed = new InstalledModuleAssembly(module);
        return CompiledDependencies.CreateCompatibilityIdResolver(
            Key,
            new Dictionary<string, string> { [Module] = installed.Mvid.ToString("N") },
            name => name == Module ? installed.Version : null,
            name => name == Module ? installed.PackageVersion : null);
    }

    private static Func<string, string?> InformationalOnly(Assembly module)
    {
        var installed = new InstalledModuleAssembly(module);
        return CompiledDependencies.CreateCompatibilityIdResolver(
            Key,
            new Dictionary<string, string> { [Module] = installed.Mvid.ToString("N") },
            name => name == Module ? installed.Version : null);
    }

    /// <summary>Emits one build of <see cref="Module"/> into its own directory and loads it in its
    /// own collectible context (several builds of one simple name coexist).</summary>
    private Assembly Load(string build, string? packageVersion)
    {
        var packageLine = packageVersion is null
            ? string.Empty
            : $"""[assembly: System.Reflection.AssemblyMetadata("{InstalledModuleAssembly.PackageVersionMetadataKey}", "{packageVersion}")]""";
        var source = $"""
            [assembly: System.Reflection.AssemblyInformationalVersion("{PlatformStamp}")]
            {packageLine}
            namespace Fixture_{build};
            public class ThreadPreparation;
            """;
        var compilation = CSharpCompilation.Create(
            Module,
            [CSharpSyntaxTree.ParseText(source)],
            PlatformReferences.Platform(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        Assert.True(result.Success, string.Join(
            Environment.NewLine, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));

        var directory = Path.Combine(root, build);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Module + ".dll");
        File.WriteAllBytes(path, buffer.ToArray());
        var context = new AssemblyLoadContext($"mw-pkgver-{build}-{Guid.NewGuid():N}", isCollectible: true);
        contexts.Add(context);
        return context.LoadFromAssemblyPath(path);
    }
}
