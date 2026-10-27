using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace MeshWeaver.PluginTester.Extensibility;

/// <summary>
/// A component that teaches <c>mw-plugin-test build-project</c> to compile a source kind the builder
/// itself does not know (Razor, for one, which MeshWeaver.Plugins supplies — the platform ships no
/// extension). The builder owns evaluation, the C# compile, the emit
/// and the generic <c>wwwroot/</c> copy; an extension owns everything specific to its source kind:
/// which items are its inputs, the source generator that turns them into C#, the assets they imply,
/// and how its own failures read.
///
/// <para>🚨 <b>Loaded, never referenced.</b> An extension lives under <c>build-extensions/&lt;name&gt;/</c>
/// beside the builder (or a directory named with <c>--extensions</c>) and is loaded into a context
/// that binds every assembly the builder already has — Roslyn, this contract — to the builder's own
/// copy. That is what makes <see cref="IBuildExtension"/> and <see cref="ISourceGenerator"/> ONE type
/// on both sides of the seam.</para>
///
/// <para><b>Nothing is silent.</b> An extension refuses by name through
/// <see cref="IProjectEvaluation.Refuse"/> or a <see cref="BuildExtensionException"/>; the builder
/// never skips a file because an extension did not claim it — a project carrying a source kind no
/// loaded extension claims is itself a refusal.</para>
/// </summary>
public interface IBuildExtension
{
    /// <summary>The extension's name — its directory under <c>build-extensions/</c>, and its prefix in the log.</summary>
    string Name { get; }

    /// <summary>
    /// The MSBuild item types whose <c>Include</c>/<c>Remove</c> operations this extension reads, in
    /// declaration order, through <see cref="IProjectEvaluation.ItemOperations"/>. A type the builder
    /// does not understand and no extension claims is a refusal, so claiming one is also what makes
    /// a project carrying it loadable.
    /// </summary>
    ImmutableArray<string> ClaimedItemTypes { get; }

    /// <summary>
    /// The file suffixes (<c>.razor</c>) this extension compiles. The builder refuses a project
    /// carrying a source kind no loaded extension claims, and the superseded-entry staleness index
    /// hands files with these suffixes to <see cref="BeginDeclaredTypeScan"/>.
    /// </summary>
    ImmutableArray<string> ClaimedSourceSuffixes { get; }

    /// <summary>The <c>--accept</c> tokens this extension understands, for the usage text.</summary>
    ImmutableArray<string> AcceptTokens { get; }

    /// <summary>
    /// The <c>build-project</c> options this extension reads (without the leading <c>--</c>), each
    /// taking one value. An option no loaded extension declares is refused rather than ignored.
    /// </summary>
    ImmutableArray<string> OptionNames { get; }

    /// <summary>
    /// Evaluates one project. Null when the project carries nothing this extension compiles.
    /// </summary>
    /// <param name="project">The builder's evaluation of the project.</param>
    /// <returns>The per-project contribution, or null.</returns>
    IProjectContribution? Evaluate(IProjectEvaluation project);

    /// <summary>
    /// Names the CAUSE when a project's compile failed in a way that points at this extension — the
    /// Razor extension reads CS0115 in a project it found no components in as "the components were
    /// never offered to the generator", not as broken source.
    /// </summary>
    /// <param name="projectName">The project's name, for the message.</param>
    /// <param name="contribution">What <see cref="Evaluate"/> returned for the project, or null.</param>
    /// <param name="errors">The declaration-pass errors.</param>
    /// <returns>A hint line, or null when the errors say nothing about this extension.</returns>
    string? DiagnoseErrors(
        string projectName, IProjectContribution? contribution, IReadOnlyList<Diagnostic> errors);

    /// <summary>
    /// Starts a scan of the repository under <paramref name="sourceRoot"/> for the top-level type
    /// names this extension's source files declare — the source side of the superseded-entry
    /// staleness check. Every name this adds can only make that check quieter.
    /// </summary>
    /// <param name="sourceRoot">The repository's source root.</param>
    /// <returns>The scan; state lives on it, never on the extension.</returns>
    IDeclaredTypeScan BeginDeclaredTypeScan(string sourceRoot);
}

/// <summary>What one extension contributes to one project.</summary>
public interface IProjectContribution
{
    /// <summary>How many input files the extension compiles here; they count as sources, so a
    /// project of nothing but components is not "a project with no source files".</summary>
    int InputCount { get; }

    /// <summary>The project's inputs as one log phrase, e.g. <c>3 Razor file(s)</c>.</summary>
    string Summary { get; }

    /// <summary>
    /// The generator run the extension needs, run BEFORE the builder's generic generators so they
    /// see its output exactly as csc would. Null when nothing needs generating.
    /// </summary>
    /// <param name="context">The run's options and environment.</param>
    /// <returns>The contribution, or null.</returns>
    /// <exception cref="BuildExtensionException">The generator this needs is missing or unusable.</exception>
    GeneratorContribution? Generators(GeneratorContext context);

    /// <summary>
    /// Writes the static assets this extension's inputs imply into the module's
    /// <c>wwwroot/</c> (after the builder copied the project's own <c>wwwroot/</c> verbatim).
    /// </summary>
    /// <param name="wwwroot">The module's output <c>wwwroot/</c>; it may not exist yet.</param>
    /// <param name="info">Where progress lines go.</param>
    /// <returns>What was written, or why the asset set cannot be reproduced — a HARD failure.</returns>
    AssetOutcome EmitAssets(string wwwroot, Action<string> info);
}

/// <summary>A source generator run an extension asks the builder for.</summary>
/// <param name="Label">The log label, e.g. <c>Razor</c>.</param>
/// <param name="Description">Where the generators came from, for the log line.</param>
/// <param name="Generators">The generators.</param>
/// <param name="AdditionalTexts">The input files, as the driver wants them.</param>
/// <param name="Options">Global and per-file analyzer-config options.</param>
/// <param name="MustProduceOutput">When true, a run that adds no document is a named failure.</param>
/// <param name="NothingProducedHint">What to check when <paramref name="MustProduceOutput"/> fires.</param>
public sealed record GeneratorContribution(
    string Label,
    string Description,
    ImmutableArray<ISourceGenerator> Generators,
    ImmutableArray<AdditionalText> AdditionalTexts,
    Microsoft.CodeAnalysis.Diagnostics.AnalyzerConfigOptionsProvider Options,
    bool MustProduceOutput,
    string NothingProducedHint);

/// <summary>The run-level inputs an extension's generator lookup may read.</summary>
/// <param name="ProjectName">The project, for messages.</param>
/// <param name="ExtensionDirectory">The extension's own directory under <c>build-extensions/</c>.</param>
/// <param name="AppDirectory">The container's assembly directory.</param>
/// <param name="Options">The values of this extension's <see cref="IBuildExtension.OptionNames"/>.</param>
/// <param name="Logger">Where load warnings go; they are never swallowed.</param>
public sealed record GeneratorContext(
    string ProjectName,
    string ExtensionDirectory,
    string AppDirectory,
    ImmutableDictionary<string, string> Options,
    Microsoft.Extensions.Logging.ILogger Logger);

/// <summary>What <see cref="IProjectContribution.EmitAssets"/> produced.</summary>
/// <param name="Summary">The written assets as one phrase (<c>2 JS module(s)</c>), or empty.</param>
/// <param name="Refusal">Non-null when the asset set cannot be reproduced; the build is RED.</param>
public sealed record AssetOutcome(string Summary, string? Refusal)
{
    /// <summary>Nothing written, nothing refused.</summary>
    public static AssetOutcome None { get; } = new(string.Empty, null);
}

/// <summary>
/// An extension's named failure — a missing or unusable generator, an input it cannot reproduce. The
/// builder turns it into a RED project result carrying the message verbatim.
/// </summary>
/// <param name="message">What is wrong, and what to do about it.</param>
public class BuildExtensionException(string message) : Exception(message);
