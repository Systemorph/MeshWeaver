using System.Collections.Concurrent;

namespace MeshWeaver.Compiler;

/// <summary>
/// Which build of each source-generator assembly (and each of a generator's own helper assemblies)
/// this process has loaded, and for which NodeType it was loaded first.
///
/// <para>Generators load into the DEFAULT assembly load context, which holds ONE assembly per
/// simple name for the life of the process. Two NodeTypes that ship different builds of the same
/// generator therefore cannot both have theirs: before #7444 whichever compiled first won and the
/// other silently ran the wrong build or lost its generator, depending on activation order (and, in
/// the parallel bake, on worker timing). The decision is to REFUSE the second build loudly:
/// <see cref="SourceGeneratorLoader"/> throws <see cref="SourceGeneratorBuildConflictException"/>,
/// which fails that NodeType's compile naming both NodeTypes and both builds. This ledger supplies
/// the FIRST NodeType's name; the default load context itself stays the authority on what is
/// loaded, so a build loaded outside this ledger is still refused (named by its location).</para>
///
/// <para>An instance, owned by whatever owns the compiles: the runtime compile service holds one
/// (a mesh singleton) and a bake holds one per run. Never static.</para>
/// </summary>
public sealed class SourceGeneratorLedger
{
    private readonly ConcurrentDictionary<string, GeneratorBuild> builds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Claims <paramref name="candidate"/>'s simple name. Returns the build already recorded under
    /// that name (which may be a different build), or <paramref name="candidate"/> itself when it
    /// is the first.
    /// </summary>
    internal GeneratorBuild Claim(GeneratorBuild candidate) => builds.GetOrAdd(candidate.Name, candidate);

    /// <summary>Withdraws a claim this caller made and then could not honour.</summary>
    internal void Withdraw(GeneratorBuild claimed)
        => builds.TryRemove(new KeyValuePair<string, GeneratorBuild>(claimed.Name, claimed));

    /// <summary>The build recorded under <paramref name="name"/>, or null.</summary>
    internal GeneratorBuild? Recorded(string name) => builds.TryGetValue(name, out var build) ? build : null;
}

/// <summary>
/// One build of a generator (or generator-helper) assembly: its simple name, version and module
/// version id (the build identity — two copies of the same bytes share it, a rebuild does not),
/// the file it was read from, and the NodeType it was loaded for.
/// </summary>
/// <param name="Name">The assembly's simple name.</param>
/// <param name="Version">The assembly version.</param>
/// <param name="Mvid">The module version id, i.e. which build this is.</param>
/// <param name="Path">The file the build was read from.</param>
/// <param name="LoadedFor">The NodeType (or project) whose compile loaded it.</param>
public sealed record GeneratorBuild(string Name, Version Version, Guid Mvid, string Path, string LoadedFor)
{
    /// <summary>True when <paramref name="other"/> is the same build (same name and module version id).</summary>
    public bool IsSameBuildAs(GeneratorBuild other)
        => string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase) && Mvid == other.Mvid;

    /// <summary>Renders the build for a refusal message.</summary>
    public override string ToString() => $"{Name} {Version} (mvid {Mvid:D}) from '{Path}'";
}

/// <summary>
/// A NodeType's compile asked for a build of a source generator (or of a generator's helper
/// assembly) that differs from the build already loaded in this process for another NodeType.
/// The default load context holds one build per name, so the compile is refused rather than run
/// against the wrong build (#7444). It is a <see cref="CompilationException"/>, so the runtime
/// compile parks the NodeType at <c>CompilationStatus.Error</c> with this message, exactly as other
/// compile refusals do.
/// </summary>
public sealed class SourceGeneratorBuildConflictException : CompilationException
{
    /// <summary>The build that was already loaded, and for which NodeType.</summary>
    public GeneratorBuild Loaded { get; }

    /// <summary>The build this compile asked for, and for which NodeType.</summary>
    public GeneratorBuild Requested { get; }

    /// <summary>Creates the refusal naming both builds and both NodeTypes.</summary>
    /// <param name="loaded">The build already loaded in the process.</param>
    /// <param name="requested">The different build this compile asked for.</param>
    public SourceGeneratorBuildConflictException(GeneratorBuild loaded, GeneratorBuild requested)
        : base(requested.LoadedFor,
            $"Source generator build conflict: '{requested.LoadedFor}' needs {requested}, but this process "
            + $"already loaded {loaded} for '{loaded.LoadedFor}'. One process can load only one build of "
            + $"'{requested.Name}', so this compile is refused rather than run against the other build. "
            + "Align both NodeTypes on the same build of the generator.")
    {
        Loaded = loaded;
        Requested = requested;
    }
}
