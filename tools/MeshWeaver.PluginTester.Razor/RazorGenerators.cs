using System.Collections.Immutable;
using System.Runtime.InteropServices;
using MeshWeaver.PluginTester.Extensibility;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginTester.Razor;

/// <summary>
/// The Razor half of <c>mw-plugin-test build-project</c>: the source generator that turns
/// <c>.razor</c> / <c>.cshtml</c> into C#, SHIPPED IN THE IMAGE and run against the container's own
/// Roslyn.
///
/// <para><b>Why this exists at all.</b> Razor compilation in the .NET SDK is not a task and not a
/// tool — it is a Roslyn <b>source generator</b> (<c>Microsoft.CodeAnalysis.Razor.Compiler</c>) that
/// emits each component as a <c>partial class</c> carrying the generated
/// <c>BuildRenderTree</c> override. Without it every component compiles to a class whose declared
/// override has nothing to override, and the build dies in a wall of <b>CS0115</b> — 15 of the 42
/// failures in the first no-SDK sweep of <c>MeshWeaver.Plugins/src</c>, more than any other
/// category. A runtime image ships no SDK, so the generator has to be laid into the image
/// deliberately; <c>razor-generators/</c> in this extension's directory is where it lands.</para>
///
/// <para>🚨 <b>The load context is the whole trick, and it is not a hack.</b> The generator is built
/// against the Roslyn of the SDK it shipped in (measured: SDK 10.0.400's copy references
/// <c>Microsoft.CodeAnalysis 5.9.0.0</c>), while the image carries the Roslyn this repo pins
/// (<c>5.6.0</c>). The default load context binds by name AND refuses a lower version, so a plain
/// <c>Assembly.LoadFrom</c> fails with <i>"Could not load … Microsoft.CodeAnalysis,
/// Version=5.9.0.0"</i> — and <c>SourceGeneratorLoader</c> correctly treats that as "not a usable
/// generator" and moves on, which is how a missing Razor generator would otherwise look exactly
/// like a <c>.razor</c> file nobody asked to compile. So generators load into a context that binds
/// every assembly the HOST already has to the host's copy, version ignored — the same thing Roslyn's
/// own analyzer loader does, and the only way the generator and the host agree on the identity of
/// <c>ISourceGenerator</c>.</para>
///
/// <para><b>Nothing here is silent.</b> A project with Razor items and no generator is a named
/// failure, not a skipped file; a generator that loads but emits nothing for a non-empty item set is
/// a named failure too. Both say what is missing and where to put it.</para>
/// </summary>
public static class RazorGenerators
{
    /// <summary>
    /// The directory, beside the builder, that carries the Razor generator. It is a directory rather
    /// than two file names because the closure is the SDK's to decide: today it is exactly
    /// <c>Microsoft.CodeAnalysis.Razor.Compiler.dll</c> + <c>Microsoft.AspNetCore.Razor.Utilities.Shared.dll</c>
    /// (measured — everything else the compiler references resolves to the host), and a future SDK
    /// adding a third is then a copy, not a code change.
    /// </summary>
    public const string DirectoryName = "razor-generators";

    /// <summary>The provenance manifest the image build writes beside the assemblies.</summary>
    public const string ManifestName = "razor-generators.json";

    /// <summary>The assembly that carries <c>RazorSourceGenerator</c>.</summary>
    public const string CompilerAssemblyName = "Microsoft.CodeAnalysis.Razor.Compiler";

    /// <summary>
    /// 🚨 The RIDs the generator is staged under, most specific first — because the SDK's copy is
    /// <b>ReadyToRun-compiled for the SDK's own RID</b> and is NOT portable. Measured: the same
    /// SDK 10.0.400 ships <c>Microsoft.CodeAnalysis.Razor.Compiler.dll</c> with PE machine
    /// <c>0xFD1D</c> on linux-x64 and <c>0xEC20</c> on osx-arm64 (the target machine XOR'd with the
    /// operating system's R2R marker), and loading the wrong one throws
    /// <see cref="BadImageFormatException"/>. The tester image is published for linux-x64 AND
    /// linux-arm64 from one x64 build host, so "copy the build machine's SDK" would have shipped an
    /// arm64 image that cannot compile a single Blazor project.
    /// </summary>
    public static ImmutableArray<string> RuntimeIdentifiers =>
    [
        .. new[]
        {
            RuntimeInformation.RuntimeIdentifier,
            // The portable form, for a host whose RID carries a distro qualifier the staging step
            // does not know about (linux-musl-x64, for one).
            $"{OperatingSystemMoniker()}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}",
        }.Distinct(StringComparer.OrdinalIgnoreCase),
    ];

    private static string OperatingSystemMoniker() =>
        OperatingSystem.IsWindows() ? "win"
        : OperatingSystem.IsMacOS() ? "osx"
        : OperatingSystem.IsLinux() ? "linux"
        : "unknown";

    /// <summary>
    /// Raised when Razor input exists and the generator that would compile it does not. Never
    /// swallowed: the alternative is an assembly that silently lacks every component in the project.
    /// </summary>
    /// <param name="message">What is missing, and where it is looked for.</param>
    public sealed class MissingRazorCompilerException(string message) : BuildExtensionException(message);

    /// <summary>The generators loaded out of one directory.</summary>
    /// <param name="Directory">Where they came from.</param>
    /// <param name="Provenance">The manifest's description of them, or a fallback naming the files.</param>
    /// <param name="Generators">The discovered <c>[Generator]</c> instances.</param>
    public sealed record Set(
        string Directory, string Provenance, ImmutableArray<ISourceGenerator> Generators);

    /// <summary>
    /// Where a Razor generator is looked for: <c>razor-generators/</c> in this extension's own
    /// directory (<c>build-extensions/razor/</c>, the shape the image publishes), then
    /// <c>razor-generators/</c> beside the builder (the shape a mounted <c>/builder</c> keeps), then
    /// <c>razor-generators/</c> under the container's <c>/app</c>.
    ///
    /// <para>🚨 <c>--razor-generators</c> REPLACES that search rather than heading it. An operator
    /// who names a directory has made a choice, and quietly falling back to the image's own copy
    /// when the named one turns out to be empty would report on a generator nobody asked for — the
    /// same class of lie as a build that resolves a reference from somewhere the command line does
    /// not mention.</para>
    /// </summary>
    /// <param name="explicitDirectory">The <c>--razor-generators</c> value, when given.</param>
    /// <param name="appDirectory">The container's assembly directory.</param>
    /// <param name="extensionDirectory">This extension's directory, when it was loaded from one.</param>
    /// <returns>The candidate directories, in the order they are tried.</returns>
    public static ImmutableArray<string> SearchPath(
        string? explicitDirectory, string appDirectory, string? extensionDirectory = null)
    {
        var bases = ImmutableArray.CreateBuilder<string>();
        if (explicitDirectory is { Length: > 0 })
        {
            bases.Add(Path.GetFullPath(explicitDirectory));
        }
        else
        {
            if (extensionDirectory is { Length: > 0 })
                bases.Add(Path.GetFullPath(Path.Combine(extensionDirectory, DirectoryName)));
            bases.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, DirectoryName)));
            if (appDirectory is { Length: > 0 })
                bases.Add(Path.GetFullPath(Path.Combine(appDirectory, DirectoryName)));
        }
        // Per-RID first (a multi-arch image stages one directory per RID), then the flat layout a
        // same-architecture build produces.
        var candidates = ImmutableArray.CreateBuilder<string>();
        foreach (var directory in bases)
        {
            foreach (var rid in RuntimeIdentifiers)
                candidates.Add(Path.Combine(directory, rid));
            candidates.Add(directory);
        }
        return [.. candidates.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Finds the first directory on the search path that actually holds the Razor compiler.
    /// </summary>
    /// <param name="explicitDirectory">The <c>--razor-generators</c> value, when given.</param>
    /// <param name="appDirectory">The container's assembly directory.</param>
    /// <param name="extensionDirectory">This extension's directory, when it was loaded from one.</param>
    /// <returns>The directory, or null when the image ships none.</returns>
    public static string? Locate(string? explicitDirectory, string appDirectory, string? extensionDirectory = null)
    {
        foreach (var candidate in SearchPath(explicitDirectory, appDirectory, extensionDirectory))
            if (Directory.Exists(candidate)
                && File.Exists(Path.Combine(candidate, CompilerAssemblyName + ".dll")))
                return candidate;
        return null;
    }

    /// <summary>
    /// The message a build gets when it has Razor input and the image has no Razor compiler. Public
    /// because it is asserted: "the generator that did not run" must be NAMED, and a message that
    /// only the failure path can produce is a message nothing checks.
    /// </summary>
    /// <param name="projectName">The project that has the Razor items.</param>
    /// <param name="razorItemCount">How many <c>.razor</c>/<c>.cshtml</c> files it has.</param>
    /// <param name="searched">The directories that were tried.</param>
    /// <returns>The failure text.</returns>
    public static string MissingCompilerMessage(
        string projectName, int razorItemCount, IEnumerable<string> searched) =>
        $"{projectName}: {razorItemCount} Razor file(s) and no Razor source generator. "
        + $"'{CompilerAssemblyName}.dll' is what turns a .razor component into the partial class "
        + "carrying its BuildRenderTree override; without it every component in this project would "
        + "compile to a class with nothing to override (a wall of CS0115) — so this fails here, by "
        + "name, rather than there, as broken-looking source. Looked in: "
        + string.Join(", ", searched)
        + $". Ship it in the image ({DirectoryName}/ beside the builder, or "
        + $"{DirectoryName}/{RuntimeInformation.RuntimeIdentifier}/ — the SDK's copy is "
        + "ReadyToRun-compiled for one RID and does not load on another) or pass "
        + "--razor-generators <dir>.";

    /// <summary>
    /// Loads the Razor generator out of <paramref name="directory"/>.
    ///
    /// <para>Every <c>*.dll</c> in the directory is a load candidate — the assemblies are the SDK's
    /// closure, not a list this repo maintains — but only types carrying
    /// <see cref="GeneratorAttribute"/> become generators.</para>
    /// </summary>
    /// <param name="directory">The directory holding the generator and its private dependencies.</param>
    /// <param name="logger">Where load failures are reported; they are never swallowed.</param>
    /// <returns>The loaded set.</returns>
    /// <exception cref="MissingRazorCompilerException">The directory holds no usable generator.</exception>
    public static Set Load(string directory, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(logger);
        var full = Path.GetFullPath(directory);
        if (!Directory.Exists(full))
            throw new MissingRazorCompilerException(
                $"--razor-generators '{full}' does not exist. A generator directory that is not there "
                + "compiles no component and silently produces a different assembly.");

        var discovery = GeneratorLoader.Discover("mw-razor-generators", full);
        var generators = discovery.Generators;
        var loadFailures = discovery.Failures;

        if (generators.IsEmpty)
            throw new MissingRazorCompilerException(
                $"'{full}' holds no usable Roslyn source generator"
                + (loadFailures.IsEmpty
                    ? ". The Razor compiler is a [Generator] type; a directory of assemblies with none "
                      + "of them compiles no component."
                    : " — " + string.Join("; ", loadFailures))
                + ".");

        foreach (var failure in loadFailures)
            logger.LogWarning("razor generator: {Failure}", failure);

        return new Set(full, ReadProvenance(full), generators);
    }

    /// <summary>
    /// Reads the image build's provenance note, or describes the files when there is none. The
    /// manifest sits at the ROOT of the staged tree, one level above the per-RID directory the
    /// generator was actually loaded from, so both are checked.
    /// </summary>
    private static string ReadProvenance(string directory)
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(directory, ManifestName),
                     Path.Combine(Path.GetDirectoryName(directory) ?? directory, ManifestName),
                 })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                return string.Join(' ', File.ReadAllLines(candidate).Select(l => l.Trim()))
                    .Replace("  ", " ", StringComparison.Ordinal);
            }
            catch (IOException)
            {
                // Fall through to the file listing: an unreadable manifest must not cost the build.
            }
        }
        return string.Join(", ", Directory
            .GetFiles(directory, "*.dll")
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal));
    }
}
