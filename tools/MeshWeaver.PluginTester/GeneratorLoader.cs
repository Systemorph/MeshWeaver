using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;

namespace MeshWeaver.PluginTester;

/// <summary>
/// How <c>build-project</c> loads a Roslyn source generator that was built against a DIFFERENT
/// Roslyn than this process carries — the one mechanism behind both
/// <see cref="RazorGenerators"/> and <see cref="StagedGenerators"/>.
///
/// <para>🚨 <b>The load context is the whole trick.</b> A generator staged out of the .NET SDK is
/// compiled against the SDK's Roslyn, and the image carries the Roslyn this repo pins — measured on
/// SDK 10.0.400 the Razor compiler wants <c>Microsoft.CodeAnalysis 5.9.0.0</c> while the image has
/// <c>5.6.0</c>. The DEFAULT load context binds by simple name and refuses a LOWER version, so a
/// plain <see cref="Assembly.LoadFrom(string)"/> throws — and a loader that reads that as "not a
/// generator" turns a missing compiler into a build indistinguishable from one nobody asked for.
/// Generators therefore load into a context that binds every assembly the HOST already has to the
/// host's copy, version IGNORED (the same thing Roslyn's own analyzer loader does). That is also
/// what keeps <see cref="ISourceGenerator"/> ONE type: a second Roslyn in this context would hand
/// the generator a different interface than the driver expects, and nothing would ever match.</para>
///
/// <para>🚨 <b>Nothing is skipped in silence.</b> Every load failure is COLLECTED and handed back
/// for the caller to report; a generator that cannot be loaded is a build fact, never a debug line.
/// The failure this whole file exists to prevent is the one that produces no error at all — an
/// assembly emitted without the half a generator would have written.</para>
/// </summary>
internal static class GeneratorLoader
{
    /// <summary>What one load pass found.</summary>
    /// <param name="Generators">The discovered <c>[Generator]</c> instances, one fresh set per call
    /// — generators hold incremental state, so a compilation never shares them with another.</param>
    /// <param name="Failures">Assemblies and types that could NOT be loaded, each already
    /// described in terms of what went wrong. Never empty-and-ignored: the caller reports them.</param>
    internal sealed record Result(
        ImmutableArray<ISourceGenerator> Generators, ImmutableArray<string> Failures);

    /// <summary>
    /// Loads every <c>[Generator]</c> type out of <paramref name="assemblies"/>.
    /// </summary>
    /// <param name="contextName">A name for the load context, for diagnosis only.</param>
    /// <param name="assemblies">The candidate assemblies, in a stable order.</param>
    /// <param name="probeDirectories">Directories searched for a generator's PRIVATE dependencies —
    /// assemblies the host does not have. Measured on the Razor compiler: with its one private
    /// dependency absent the compiler assembly still loads and its types still enumerate, and every
    /// call into it throws, which is the exact "generator silently produces nothing" shape this
    /// design exists to prevent.</param>
    /// <returns>The generators, and everything that failed to load.</returns>
    internal static Result Load(
        string contextName,
        IReadOnlyList<string> assemblies,
        IReadOnlyList<string> probeDirectories)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextName);
        ArgumentNullException.ThrowIfNull(assemblies);
        ArgumentNullException.ThrowIfNull(probeDirectories);

        var context = new HostFirstLoadContext(contextName, probeDirectories);
        var generators = ImmutableArray.CreateBuilder<ISourceGenerator>();
        var failures = ImmutableArray.CreateBuilder<string>();
        foreach (var dll in assemblies)
        {
            Assembly assembly;
            try
            {
                assembly = context.LoadFromAssemblyPath(dll);
            }
            catch (BadImageFormatException ex)
            {
                // 🚨 The architecture trap, NAMED. A generator the SDK ReadyToRun-compiled for its
                // own RID fails EXACTLY here when the image staged the build host's copy into
                // another architecture's leg — and "incorrect format" on its own sends the reader
                // hunting for a corrupt file instead of a mis-staged image.
                failures.Add(
                    $"{Path.GetFileName(dll)}: {ex.Message.TrimEnd()} — this is what a generator built "
                    + $"for another architecture looks like. This process is "
                    + $"{RuntimeInformation.RuntimeIdentifier}; a ReadyToRun-compiled generator runs on "
                    + "exactly one RID, so the image must stage the copy for THIS one.");
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
                // 🚨 The signature of the version skew this loader exists to defeat, kept LOUD:
                // Roslyn's own loader logs this at debug and moves on, which is how "the generator
                // did not load" turns into "those files were never compiled". The types that DID
                // load are salvaged — an analyzer assembly whose code-fix half needs an IDE
                // assembly still yields its generator.
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

        return new Result(generators.ToImmutable(), failures.ToImmutable());
    }

    /// <summary>
    /// Every <c>*.dll</c> in <paramref name="directory"/>, ordered — the candidate set for a staged
    /// generator directory. A directory rather than a file list because the closure is the SDK's (or
    /// the package's) to decide: a future version adding a private dependency is then a copy, not a
    /// code change.
    /// </summary>
    internal static ImmutableArray<string> AssembliesIn(string directory) =>
        [.. Directory.GetFiles(directory, "*.dll").OrderBy(p => p, StringComparer.Ordinal)];

    /// <summary>
    /// A load context that binds every assembly the HOST already has to the host's copy, IGNORING
    /// the version the generator asked for, and loads only what the host does not have from the
    /// generator's own directories.
    ///
    /// <para>🚨 Both halves are load-bearing. Binding Roslyn to the host is what lets a generator
    /// built against a different Roslyn run at all, and it is what keeps
    /// <see cref="ISourceGenerator"/> a single type. Probing the staged directories is what makes a
    /// generator with a private dependency work at all rather than throwing on first call.</para>
    /// </summary>
    private sealed class HostFirstLoadContext : AssemblyLoadContext
    {
        private readonly ImmutableDictionary<string, string> _local;

        internal HostFirstLoadContext(string name, IReadOnlyList<string> probeDirectories)
            : base(name, isCollectible: false)
        {
            var local = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var directory in probeDirectories)
            {
                if (!Directory.Exists(directory)) continue;
                foreach (var path in Directory.GetFiles(directory, "*.dll"))
                    local[Path.GetFileNameWithoutExtension(path)] = path;
            }
            _local = local.ToImmutable();
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is not { Length: > 0 } name)
                return null;
            try
            {
                // Version-BLIND on purpose: `new AssemblyName(name)` carries no version, so the
                // default context resolves by simple name and hands back whatever the host ships.
                return Default.LoadFromAssemblyName(new AssemblyName(name));
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                // Not a host assembly — fall through to the generator's own directories.
            }
            return _local.TryGetValue(name, out var path) ? LoadFromAssemblyPath(path) : null;
        }
    }
}
