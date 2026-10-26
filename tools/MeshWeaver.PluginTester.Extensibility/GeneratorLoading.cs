using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace MeshWeaver.PluginTester.Extensibility;

/// <summary>
/// A load context that binds every assembly the HOST already has to the host's copy, IGNORING the
/// version asked for, and loads only what the host does not have from one directory.
///
/// <para>🚨 Both halves are load-bearing. Binding Roslyn to the host is what lets a generator built
/// against a newer Roslyn run at all (the SDK's Razor compiler wants a newer
/// <c>Microsoft.CodeAnalysis</c> than the image pins) — and, more importantly, it is what keeps
/// <see cref="ISourceGenerator"/> and <see cref="IBuildExtension"/> ONE type: a second copy loaded
/// here would give the loaded code a different interface than the builder expects, and nothing would
/// ever match. Loading a private dependency from the directory is what makes the loaded code work at
/// all: with it absent the assembly loads, its types enumerate, and every call into it throws.</para>
/// </summary>
public sealed class HostFirstLoadContext : AssemblyLoadContext
{
    private readonly ImmutableDictionary<string, string> _local;

    /// <summary>A context over the assemblies in <paramref name="directory"/>.</summary>
    /// <param name="name">The context's name, for diagnostics.</param>
    /// <param name="directory">Where the non-host assemblies live.</param>
    public HostFirstLoadContext(string name, string directory)
        : base(name, isCollectible: false)
    {
        _local = Directory
            .GetFiles(directory, "*.dll")
            .ToImmutableDictionary(
                p => Path.GetFileNameWithoutExtension(p), p => p, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not { Length: > 0 } name)
            return null;
        try
        {
            // Version-BLIND on purpose: `new AssemblyName(name)` carries no version, so the default
            // context resolves by simple name and hands back whatever the host ships.
            return Default.LoadFromAssemblyName(new AssemblyName(name));
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            // Not a host assembly — fall through to the directory.
        }
        return _local.TryGetValue(name, out var path) ? LoadFromAssemblyPath(path) : null;
    }
}

/// <summary>
/// Finds the <c>[Generator]</c> types in a directory, through a <see cref="HostFirstLoadContext"/>,
/// and reports every load failure by name.
///
/// <para>🚨 Roslyn's own loader logs a failed generator load at debug and moves on, which is how
/// "the generator did not load" turns into "the input was never compiled". Here every failure is
/// returned to the caller, and an architecture mismatch (a ReadyToRun generator built for another
/// RID) is named as one.</para>
/// </summary>
public static class GeneratorLoader
{
    /// <summary>What <see cref="Discover"/> found.</summary>
    /// <param name="Generators">The instantiated generators.</param>
    /// <param name="Failures">One line per assembly or type that could not be loaded.</param>
    public sealed record Discovery(ImmutableArray<ISourceGenerator> Generators, ImmutableArray<string> Failures);

    /// <summary>Loads every <c>[Generator]</c> in <paramref name="directory"/>.</summary>
    /// <param name="contextName">The load context's name.</param>
    /// <param name="directory">The directory holding the generator and its private dependencies.</param>
    /// <returns>The generators and the failures.</returns>
    public static Discovery Discover(string contextName, string directory)
    {
        var context = new HostFirstLoadContext(contextName, directory);
        var generators = ImmutableArray.CreateBuilder<ISourceGenerator>();
        var failures = ImmutableArray.CreateBuilder<string>();
        foreach (var dll in Directory.GetFiles(directory, "*.dll").OrderBy(p => p, StringComparer.Ordinal))
        {
            Assembly assembly;
            try
            {
                assembly = context.LoadFromAssemblyPath(dll);
            }
            catch (BadImageFormatException ex)
            {
                // 🚨 The architecture trap, named. The SDK crossgens its generators for the SDK's own
                // RID, so a copy staged from another architecture fails EXACTLY here — and
                // "incorrect format" on its own sends the reader hunting for a corrupt file.
                failures.Add(
                    $"{Path.GetFileName(dll)}: {ex.Message.TrimEnd()} — this is what a generator "
                    + $"built for another architecture looks like. This process is "
                    + $"{RuntimeInformation.RuntimeIdentifier}; an SDK generator is ReadyToRun-compiled "
                    + "for exactly one RID, so the image must stage the copy for THIS one.");
                continue;
            }
            catch (Exception ex) when (ex is FileLoadException or FileNotFoundException)
            {
                failures.Add($"{Path.GetFileName(dll)}: {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            Type?[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // 🚨 The signature of the version skew the load context exists to defeat, kept LOUD.
                failures.Add(
                    $"{Path.GetFileName(dll)}: {ex.LoaderExceptions.FirstOrDefault()?.Message ?? ex.Message}");
                types = ex.Types;
            }

            foreach (var type in types)
            {
                if (type is null || type.IsAbstract) continue;
                if (type.GetCustomAttributes(typeof(GeneratorAttribute), inherit: false).Length == 0) continue;
                if (type.GetConstructor(Type.EmptyTypes) is null) continue;
                try
                {
                    switch (Activator.CreateInstance(type))
                    {
                        case IIncrementalGenerator incremental:
                            generators.Add(incremental.AsSourceGenerator());
                            break;
                        case ISourceGenerator source:
                            generators.Add(source);
                            break;
                        default:
                            break;
                    }
                }
                catch (Exception ex) when (ex is TargetInvocationException or MissingMethodException
                                               or TypeLoadException or FileNotFoundException)
                {
                    failures.Add($"{type.FullName}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        return new Discovery(generators.ToImmutable(), failures.ToImmutable());
    }
}

/// <summary>One file on disk, as Roslyn's generator driver wants an additional input.</summary>
/// <param name="path">Absolute path.</param>
public sealed class FileAdditionalText(string path) : AdditionalText
{
    /// <inheritdoc />
    public override string Path { get; } = path;

    /// <inheritdoc />
    public override SourceText? GetText(CancellationToken cancellationToken = default)
    {
        try
        {
            using var stream = File.OpenRead(Path);
            return SourceText.From(stream, Encoding.UTF8);
        }
        catch (IOException)
        {
            // The driver treats a null text as "no content"; the generator then reports its own
            // diagnostic naming the file, which is a better message than an exception here.
            return null;
        }
    }
}

/// <summary>
/// Analyzer-config options from plain dictionaries: one global set (<c>build_property.*</c>) and one
/// set per additional file (<c>build_metadata.AdditionalFiles.*</c>), keyed by the file's path.
/// </summary>
public sealed class DictionaryOptionsProvider : AnalyzerConfigOptionsProvider
{
    private readonly ImmutableDictionary<string, AnalyzerConfigOptions> _perFile;

    /// <summary>Builds the provider.</summary>
    /// <param name="global">The global options.</param>
    /// <param name="perFile">Options per additional-file path.</param>
    public DictionaryOptionsProvider(
        IReadOnlyDictionary<string, string> global,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> perFile)
    {
        GlobalOptions = new Options(global.ToImmutableDictionary(StringComparer.Ordinal));
        _perFile = perFile.ToImmutableDictionary(
            e => e.Key,
            e => (AnalyzerConfigOptions)new Options(e.Value.ToImmutableDictionary(StringComparer.Ordinal)),
            StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public override AnalyzerConfigOptions GlobalOptions { get; }

    /// <inheritdoc />
    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => Options.Empty;

    /// <inheritdoc />
    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) =>
        _perFile.GetValueOrDefault(textFile.Path, Options.Empty);

    private sealed class Options(ImmutableDictionary<string, string> values) : AnalyzerConfigOptions
    {
        internal static readonly Options Empty = new(ImmutableDictionary<string, string>.Empty);

        public override bool TryGetValue(string key, out string value) =>
            values.TryGetValue(key, out value!);
    }
}
