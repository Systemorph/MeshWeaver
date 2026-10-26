using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using MeshWeaver.PluginTester.Extensibility;

namespace MeshWeaver.PluginTester;

/// <summary>A build extension, loaded, with the directory it came from.</summary>
/// <param name="Extension">The extension.</param>
/// <param name="Directory">Its directory — where it looks for what it ships (a generator, say).</param>
public sealed record LoadedBuildExtension(IBuildExtension Extension, string Directory);

/// <summary>
/// Finds and loads the BUILD EXTENSIONS <c>build-project</c> compiles with — the components that know
/// a source kind the builder itself does not (Razor is the first;
/// <see href="/Doc/Architecture/CoreCarriesNoGuiTechnology">Core Carries No GUI Technology</see>).
///
/// <para>An extension is a directory holding <see cref="ManifestName"/> (which names its assembly)
/// beside that assembly and whatever it ships. The standard search is <c>build-extensions/*/</c>
/// beside this builder, then under the container's <c>/app</c>; an extension name found twice is
/// taken from the first.</para>
///
/// <para>🚨 <c>--extensions &lt;dir&gt;</c> REPLACES that search rather than heading it, exactly as
/// <c>--module-libs</c> and <c>--razor-generators</c> do: an operator who names extensions has made
/// a choice, and quietly adding the image's own would build with a set nobody asked for. A named
/// directory that does not exist, or holds no extension, is a failure by name.</para>
///
/// <para>The assembly loads through a <see cref="HostFirstLoadContext"/>, so the extension binds
/// Roslyn and <see cref="IBuildExtension"/> to THIS process's copies — and, when the host already
/// carries the extension assembly itself (a test that references it), the host's copy is the one
/// used, so the extension's types are the ones the caller sees.</para>
/// </summary>
public static class BuildExtensions
{
    /// <summary>The directory, beside the builder, holding one subdirectory per extension.</summary>
    public const string DirectoryName = "build-extensions";

    /// <summary>The file naming an extension's assembly: <c>{ "assembly": "&lt;simple name&gt;" }</c>.</summary>
    public const string ManifestName = "build-extension.json";

    /// <summary>
    /// The candidate extension directories, in the order they are tried.
    /// </summary>
    /// <param name="explicitDirectories">The <c>--extensions</c> values; non-empty replaces the search.</param>
    /// <param name="appDirectory">The container's assembly directory.</param>
    /// <returns>Directories that each hold one extension.</returns>
    public static ImmutableArray<string> SearchPath(IReadOnlyList<string> explicitDirectories, string appDirectory)
    {
        if (explicitDirectories.Count > 0)
            return [.. explicitDirectories.Select(Path.GetFullPath)];
        var roots = new List<string> { Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, DirectoryName)) };
        if (appDirectory is { Length: > 0 })
            roots.Add(Path.GetFullPath(Path.Combine(appDirectory, DirectoryName)));
        return
        [
            .. roots
                .Distinct(StringComparer.Ordinal)
                .Where(Directory.Exists)
                .SelectMany(root => Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
                .Where(d => File.Exists(Path.Combine(d, ManifestName))),
        ];
    }

    /// <summary>Loads every extension on the search path.</summary>
    /// <param name="explicitDirectories">The <c>--extensions</c> values.</param>
    /// <param name="appDirectory">The container's assembly directory.</param>
    /// <returns>The loaded extensions, one per name.</returns>
    /// <exception cref="InvalidOperationException">A named directory is missing or holds no extension,
    /// or an extension cannot be loaded.</exception>
    public static ImmutableArray<LoadedBuildExtension> Load(IReadOnlyList<string> explicitDirectories, string appDirectory)
    {
        foreach (var named in explicitDirectories.Select(Path.GetFullPath))
        {
            if (!Directory.Exists(named))
                throw new InvalidOperationException(
                    $"--extensions '{named}' does not exist. An extension directory that is not there "
                    + "compiles nothing it claims, and every project carrying that source kind would be "
                    + "refused as if no extension existed.");
            if (!File.Exists(Path.Combine(named, ManifestName)))
                throw new InvalidOperationException(
                    $"--extensions '{named}' holds no {ManifestName}, so it is not a build extension.");
        }

        var loaded = ImmutableArray.CreateBuilder<LoadedBuildExtension>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in SearchPath(explicitDirectories, appDirectory))
            foreach (var extension in LoadOne(directory))
                if (names.Add(extension.Name))
                    loaded.Add(new LoadedBuildExtension(extension, directory));
        return loaded.ToImmutable();
    }

    private static IEnumerable<IBuildExtension> LoadOne(string directory)
    {
        var manifestPath = Path.Combine(directory, ManifestName);
        string assemblyName;
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            assemblyName = manifest.RootElement.GetProperty("assembly").GetString() ?? string.Empty;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IOException)
        {
            throw new InvalidOperationException(
                $"{manifestPath} could not be read ({ex.GetType().Name}: {ex.Message}) — it must be "
                + "{ \"assembly\": \"<simple name>\" }.");
        }
        if (assemblyName.Length == 0)
            throw new InvalidOperationException($"{manifestPath} names no assembly.");

        Assembly assembly;
        try
        {
            assembly = new HostFirstLoadContext($"mw-build-extension-{Path.GetFileName(directory)}", directory)
                .LoadFromAssemblyName(new AssemblyName(assemblyName));
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            throw new InvalidOperationException(
                $"build extension '{assemblyName}' in {directory} could not be loaded: {ex.Message}");
        }

        var extensions = assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(IBuildExtension).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null)
            .Select(t => (IBuildExtension)Activator.CreateInstance(t)!)
            .ToImmutableArray();
        if (extensions.IsEmpty)
            throw new InvalidOperationException(
                $"build extension '{assemblyName}' in {directory} declares no {nameof(IBuildExtension)} "
                + $"with a parameterless constructor — or binds a different copy of "
                + $"MeshWeaver.PluginTester.Extensibility than this builder, which makes it a different type.");
        return extensions;
    }
}
