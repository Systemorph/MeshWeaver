namespace MeshWeaver.Mesh;

/// <summary>
/// One module as the boot loader is asked to install it: the generation it SHOULD run, and — when
/// the deployment still holds one — the generation it ran BEFORE, to fall back on when the newest
/// cannot load on this platform (#3649, rule R1 of the module adoption policy).
///
/// <para>🚨 <b>Why a fallback and not a refusal.</b> Until #3649 a landed generation that did not
/// load here — refused by the link probe, or faulting in <c>Assembly.LoadFrom</c> — left the module
/// ABSENT unless the image happened to ship a baseline copy (#2548). A Store-only module has no
/// such copy, so a shelved landing built for a newer platform took a working module away: the
/// previous generation was still on the volume, but nothing referenced it any more and the next GC
/// pass removed it. The maintainer's rule of 2026-09-07 is the opposite: <i>an installation always
/// runs SOME version of every module it has installed — the newest one that loads</i>.</para>
///
/// <para>The previous generation is resolved LAZILY, through <see cref="Previous"/>, because the
/// portal pins every generation it loads to process-local storage (a per-boot copy of the whole
/// directory, #2509) and copying every module's previous generation on every boot would double
/// that cost for a path that is taken only when the newest generation fails. A candidate with no
/// <see cref="Previous"/> installs exactly as a bare path always did.</para>
/// </summary>
/// <param name="Location">The entry DLL of the generation to load — the newest one landed.</param>
public sealed record ModuleInstallCandidate(string Location)
{
    /// <summary>
    /// Resolves the entry DLL of the PREVIOUS generation, or null when the deployment holds none
    /// (a first install, a legacy fixed-folder module, a baseline entry). Invoked at most once, and
    /// only after <see cref="Location"/> was refused before loading or failed to load — never for
    /// a generation that loaded. A resolver that throws counts as "no previous generation" and
    /// is reported on stderr, because at this point in boot nothing else exists to report to.
    /// </summary>
    public Func<string?>? Previous { get; init; }

    /// <summary>The package version <see cref="Location"/> was landed at, for the report — a
    /// row that says "v1.3.0 (gen B) does not load here" is one an operator can act on; a bare
    /// generation id is not. Null when unrecorded.</summary>
    public string? Version { get; init; }

    /// <summary>The package version the previous generation was landed at, for the report.</summary>
    public string? PreviousVersion { get; init; }
}

/// <summary>
/// One module that is RUNNING ITS PREVIOUS GENERATION because the newest one landed cannot load on
/// this platform (#3649) — registered as an enumerable DI singleton beside
/// <see cref="InstalledModuleAssembly"/> and <see cref="IncompatibleModule"/>, so every status
/// surface can name it as what it is: present, and behind.
///
/// <para>🚨 It is NOT an <see cref="IncompatibleModule"/>. That record means "contributes nothing,
/// this replica is degraded", and it is what turns a required module Incompatible and a package
/// card into "not running here". A fallback module IS running — its assembly is loaded, its nodes
/// and services are registered, its features work — so a readiness probe stays Healthy on it and
/// <c>Modules:Required</c> classifies it Present. What the record carries is the one fact a
/// person needs beside "present": which generation this is, which one it is not, and why.</para>
///
/// <para>🚨 Nor is it "restart required". A restart re-runs the same measurement on the same
/// bytes and falls back again; the prompt would be one no restart can clear. The newest
/// generation starts running by itself when a build of it that loads here ships, or when the
/// platform moves — both of which are restarts (#3650 makes the first of those happen).</para>
/// </summary>
/// <param name="Name">The module's assembly simple name.</param>
/// <param name="Entry">The entry DLL of the newest generation — the one that does NOT load here.</param>
/// <param name="PreviousEntry">The entry DLL of the generation that loaded instead.</param>
/// <param name="Reason">Why the newest generation cannot load here — the link probe's report
/// naming the missing type, or the load exception's type and message.</param>
public sealed record FallbackModule(string Name, string Entry, string PreviousEntry, string Reason)
{
    /// <summary>The package version of the newest generation, when recorded.</summary>
    public string? Version { get; init; }

    /// <summary>The package version of the generation that loaded, when recorded.</summary>
    public string? PreviousVersion { get; init; }

    /// <summary>The generation directory leaf of the newest generation (<c>&lt;name&gt;@&lt;id&gt;</c>)
    /// — the same string the activation record's <c>Directory</c> carries, so a surface can match
    /// this record against the record that landed it.</summary>
    public string Generation => LeafOf(Entry);

    /// <summary>The generation directory leaf of the generation that loaded.</summary>
    public string PreviousGeneration => LeafOf(PreviousEntry);

    /// <summary>
    /// The boot-log line: which module, which generation it runs, which one it does not, and why.
    /// Written to stderr by <c>MeshBuilder.InstallModules</c> (nothing else exists that early) and
    /// re-logged as a Warning once the logging pipeline is up.
    /// </summary>
    public string Report() =>
        $"'{Name}' runs its previous generation {Label(PreviousVersion, PreviousGeneration)} because "
        + $"{Label(Version, Generation)} cannot load here: {Reason}";

    /// <summary>
    /// The status-row sentence — "runs v1.2.3 (gen A); v1.3.0 (gen B) landed but does not load
    /// here: …" — shared by every surface so an operator and a package card never read two
    /// different stories about one module.
    /// </summary>
    public string Describe() =>
        $"runs {Label(PreviousVersion, PreviousGeneration)}; {Label(Version, Generation)} landed "
        + $"but does not load here: {Reason}";

    private static string Label(string? version, string generation) =>
        string.IsNullOrWhiteSpace(version) ? $"({generation})" : $"v{version} ({generation})";

    private static string LeafOf(string entry)
    {
        var directory = Path.GetDirectoryName(entry);
        return string.IsNullOrEmpty(directory) ? entry : Path.GetFileName(directory);
    }
}
