using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json;
using MeshWeaver.Mesh;

namespace MeshWeaver.PluginTester;

/// <summary>
/// 🚨 <b>The static compatibility gate — <c>mw-plugin-test platform-link</c></b> (policy
/// <c>platform-backwards-compatibility</c>, Doc/Architecture/PlatformCompatibilityLadder).
///
/// <para>Rung 2 of the ladder, measured without running anything: the plugin bytes the fleet runs
/// NOW (the deployed module bundles, compiled against an earlier platform) against a CANDIDATE
/// platform (this pull request's core build, or the image CD just promoted). Every type, assembly
/// version and — the reason this verb exists — every MEMBER those bytes reference is resolved
/// against the candidate with <see cref="ModulePlatformLink"/> and
/// <see cref="ModuleLinkOptions.WithMembers"/>: the one probe, never a second one.</para>
///
/// <para>A break is only acceptable when it is DECLARED: the pull request moves the compatibility
/// epoch (<c>src/MeshWeaver.Compiler/platform-compatibility.json</c>) and lists every broken member
/// under that epoch's break entry. Everything else is red — a break with no declaration, a
/// declaration that misses a member, and an epoch bump with no break to justify it
/// (<see cref="Decide"/>).</para>
/// </summary>
internal static class PlatformLink
{
    /// <summary>The verb.</summary>
    public const string Verb = "platform-link";

    /// <summary>A bundle's module payload directory, as the pack lane writes it.</summary>
    private const string ModulesFolder = "meshweaver/modules/";

    /// <summary>One module assembly's verdict.</summary>
    /// <param name="Bundle">The bundle file it came from.</param>
    /// <param name="Verdict">The probe's verdict.</param>
    public sealed record ModuleResult(string Bundle, ModuleLinkVerdict Verdict);

    /// <summary>What the gate decided, and why, in one sentence per finding.</summary>
    /// <param name="Green">True when the pull request may merge on this check.</param>
    /// <param name="Findings">Every line the log must carry, red or advisory.</param>
    public sealed record Decision(bool Green, ImmutableArray<string> Findings);

    /// <summary>A declared break, as <c>platform-compatibility.json</c> carries it.</summary>
    /// <param name="Epoch">The epoch the break introduced.</param>
    /// <param name="Members">The members it declares broken, <c>Type::Member</c> by assembly.</param>
    public sealed record DeclaredBreak(int Epoch, ImmutableArray<(string Assembly, string Member)> Members);

    /// <summary>
    /// The pure decision over the measured verdicts and the declaration.
    /// </summary>
    /// <param name="results">Every module assembly checked.</param>
    /// <param name="baseEpoch">The compatibility epoch at the merge base (null when unknown — no
    /// declaration file there yet).</param>
    /// <param name="headEpoch">The epoch this candidate declares (null when unknown).</param>
    /// <param name="declared">The declared breaks.</param>
    public static Decision Decide(
        IReadOnlyList<ModuleResult> results, int? baseEpoch, int? headEpoch,
        IReadOnlyList<DeclaredBreak> declared)
    {
        var findings = ImmutableArray.CreateBuilder<string>();
        if (results.Count == 0)
            return new Decision(false, ["platform-link: NO module assembly was checked — a compatibility verdict over nothing is not a verdict. The plugin set did not arrive; fix the input, never the gate."]);

        var refused = results.Where(r => !r.Verdict.MayLoad).ToArray();
        var epochMoved = baseEpoch is { } b && headEpoch is { } h && h != b;
        var green = true;

        if (epochMoved && headEpoch < baseEpoch)
        {
            findings.Add($"platform-link: the compatibility epoch moved BACKWARDS ({baseEpoch} → {headEpoch}). An epoch only ever increases.");
            green = false;
        }

        if (refused.Length == 0)
        {
            if (epochMoved)
            {
                findings.Add($"platform-link: the compatibility epoch moved ({baseEpoch} → {headEpoch}) but NO deployed plugin breaks on this platform. "
                             + "An epoch bump forces every plugin in the fleet to rebuild; a gratuitous one is refused. Revert the bump, or name the break it declares.");
                green = false;
            }
            return new Decision(green, findings.ToImmutable());
        }

        // Declared names are full type names (`Ns.Type`) or members (`Ns.Type::Member`); a verdict
        // line starts with that name and continues with a space (`(Assembly)`, `[signature]`).
        var listed = declared
            .Where(d => headEpoch is { } e && d.Epoch == e)
            .SelectMany(d => d.Members)
            .Select(m => m.Member.Trim())
            .Where(m => m.Length > 0)
            .ToImmutableArray();

        foreach (var result in refused)
        {
            var verdict = result.Verdict;
            if (verdict.State != ModuleLinkState.Unlinkable || !epochMoved)
            {
                findings.Add($"platform-link: {result.Bundle}: {verdict.Report()}");
                green = false;
                continue;
            }
            // A declared break: every missing reference must be named by the declaration.
            var undeclared = verdict.MissingTypes.Concat(verdict.MissingMembers)
                .Where(m => !listed.Any(l => m.StartsWith(l + " ", StringComparison.Ordinal)
                                             || m.Contains(" " + l + " ", StringComparison.Ordinal)))
                .ToArray();
            if (undeclared.Length > 0)
            {
                findings.Add($"platform-link: {result.Bundle}: the epoch moved to {headEpoch}, but these breaks are NOT in its declaration: {string.Join("; ", undeclared)}");
                green = false;
            }
            else
                findings.Add($"platform-link: {result.Bundle}: DECLARED break under epoch {headEpoch} — '{verdict.Module}' must rebuild against this platform (its ceiling is the last build of epoch {baseEpoch}).");
        }
        if (!epochMoved)
            findings.Add("platform-link: RED — a deployed plugin cannot run on this platform. Within a major the platform is backwards compatible (policy platform-backwards-compatibility): restore the member (an [Obsolete] forwarder keeps the old signature), fix the binder, or — only when the break is deliberate — bump \"epoch\" in src/MeshWeaver.Compiler/platform-compatibility.json and declare every broken member there. Never a seal, a pin or a rebuild-everything fallback.");
        return new Decision(green, findings.ToImmutable());
    }

    /// <summary>Reads the declaration file: (epoch, breaks). A missing file is (null, []) — the
    /// state before the file existed; a malformed one throws, never reads as "no breaks".</summary>
    public static (int? Epoch, ImmutableArray<DeclaredBreak> Breaks) ReadDeclaration(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return (null, []);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (!root.TryGetProperty("epoch", out var epochElement) || !epochElement.TryGetInt32(out var epoch))
            throw new InvalidDataException($"{path}: no integer \"epoch\"");
        var breaks = ImmutableArray.CreateBuilder<DeclaredBreak>();
        if (root.TryGetProperty("breaks", out var breaksElement) && breaksElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in breaksElement.EnumerateArray())
            {
                if (!entry.TryGetProperty("epoch", out var e) || !e.TryGetInt32(out var breakEpoch))
                    throw new InvalidDataException($"{path}: a break entry carries no integer \"epoch\"");
                var members = ImmutableArray.CreateBuilder<(string, string)>();
                if (entry.TryGetProperty("members", out var membersElement) && membersElement.ValueKind == JsonValueKind.Array)
                    foreach (var member in membersElement.EnumerateArray())
                        members.Add((member.GetProperty("assembly").GetString() ?? "", member.GetProperty("member").GetString() ?? ""));
                breaks.Add(new DeclaredBreak(breakEpoch, members.ToImmutable()));
            }
        }
        return (epoch, breaks.ToImmutable());
    }

    /// <summary>
    /// Checks every module assembly of every bundle against <paramref name="surface"/>. A bundle's
    /// <c>meshweaver/modules/*.dll</c> are extracted into their own directory, so each module's
    /// closure is exactly its bundle. The subjects are the bundle's own <c>MeshWeaver.*</c>
    /// assemblies; a third-party dependency and a platform copy that rode along are not.
    /// </summary>
    public static IReadOnlyList<ModuleResult> CheckBundles(
        IEnumerable<string> bundles, ModulePlatformSurface surface, ModuleLinkOptions options, string workDirectory)
    {
        var results = new List<ModuleResult>();
        foreach (var bundle in bundles.OrderBy(b => b, StringComparer.Ordinal))
        {
            var target = Path.Combine(workDirectory, Path.GetFileNameWithoutExtension(bundle) + "-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(target);
            using (var archive = ZipFile.OpenRead(bundle))
            {
                foreach (var entry in archive.Entries)
                {
                    var name = entry.FullName.Replace('\\', '/');
                    if (!name.StartsWith(ModulesFolder, StringComparison.OrdinalIgnoreCase)
                        || name.Length == ModulesFolder.Length
                        || name[ModulesFolder.Length..].Contains('/')
                        || !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        continue;
                    entry.ExtractToFile(Path.Combine(target, Path.GetFileName(name)), overwrite: true);
                }
            }
            var dlls = Directory.GetFiles(target, "*.dll");
            if (dlls.Length == 0)
                throw new InvalidDataException($"{Path.GetFileName(bundle)}: no {ModulesFolder}*.dll in the bundle — not a module bundle");
            foreach (var dll in dlls.OrderBy(d => d, StringComparer.Ordinal))
            {
                var name = Path.GetFileNameWithoutExtension(dll);
                // The module's OWN assemblies are the subject; a third-party dependency riding in
                // the bundle references no platform surface, and a platform copy that rode along is
                // not what the loader binds.
                if (!name.StartsWith(ModulePlatformLink.PlatformAssemblyPrefix, StringComparison.Ordinal)
                    || surface.IsPlatformBound(name))
                    continue;
                results.Add(new ModuleResult(Path.GetFileName(bundle), ModulePlatformLink.Check(dll, surface, options)));
            }
        }
        return results;
    }

    /// <summary>A directory host's surface: its own DLLs first, then the NEWEST version of each
    /// shared framework under <paramref name="sharedFrameworks"/> — the binding precedence of a
    /// framework-dependent app.</summary>
    public static ModulePlatformSurface DirectorySurface(string appDirectory, string sharedFrameworks)
    {
        var files = Directory.GetFiles(appDirectory, "*.dll").OrderBy(f => f, StringComparer.Ordinal).ToList();
        foreach (var framework in Directory.GetDirectories(sharedFrameworks).OrderBy(d => d, StringComparer.Ordinal))
        {
            var newest = Directory.GetDirectories(framework)
                .Select(d => (Path: d, Version: Version.TryParse(Path.GetFileName(d).Split('-')[0], out var v) ? v : null))
                .Where(d => d.Version is not null)
                .OrderByDescending(d => d.Version)
                .Select(d => d.Path)
                .FirstOrDefault();
            if (newest is not null)
                files.AddRange(Directory.GetFiles(newest, "*.dll").OrderBy(f => f, StringComparer.Ordinal));
        }
        return ModulePlatformSurface.OfFiles(files);
    }

    /// <summary>Runs the verb. Exit 0 green, 1 red, 2 bad usage.</summary>
    public static int Run(string[] args)
    {
        string? app = null;
        string? shared = null;
        string? declaration = null;
        int? baseEpoch = null;
        var host = "container";
        var bundles = new List<string>();
        var judgeFrom = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--shared-frameworks" when i + 1 < args.Length: shared = args[++i]; break;
                case "--bundle" when i + 1 < args.Length: bundles.Add(args[++i]); break;
                case "--bundles-dir" when i + 1 < args.Length:
                    var dir = args[++i];
                    if (!Directory.Exists(dir)) { Console.Error.WriteLine($"platform-link: '{dir}' does not exist."); return 2; }
                    bundles.AddRange(Directory.GetFiles(dir, "*.nupkg", SearchOption.AllDirectories));
                    bundles.AddRange(Directory.GetFiles(dir, "*.zip", SearchOption.AllDirectories));
                    break;
                case "--judge-from" when i + 1 < args.Length: judgeFrom.Add(args[++i]); break;
                case "--declaration" when i + 1 < args.Length: declaration = args[++i]; break;
                case "--base-epoch" when i + 1 < args.Length:
                    if (!int.TryParse(args[++i], out var parsed)) { Console.Error.WriteLine("platform-link: --base-epoch needs an integer."); return 2; }
                    baseEpoch = parsed;
                    break;
                case "--host" when i + 1 < args.Length: host = args[++i]; break;
                case "--help" or "-h":
                    Console.WriteLine("usage: mw-plugin-test platform-link <app-dir> --shared-frameworks <dir> "
                                      + "(--bundle <file>… | --bundles-dir <dir>) [--host container|directory] "
                                      + "[--judge-from <dir>]… [--declaration <platform-compatibility.json>] [--base-epoch <n>]");
                    return 0;
                default:
                    if (args[i].StartsWith('-') || app is not null)
                    {
                        Console.Error.WriteLine($"platform-link: unknown or incomplete argument '{args[i]}'. Try --help.");
                        return 2;
                    }
                    app = args[i];
                    break;
            }
        }
        if (app is null || shared is null)
        {
            Console.Error.WriteLine("platform-link: <app-dir> and --shared-frameworks are both required — the candidate platform is its /app AND its <dotnet root>/shared.");
            return 2;
        }
        if (bundles.Count == 0)
        {
            Console.Error.WriteLine("platform-link: no bundle to check (--bundle / --bundles-dir matched nothing). A verdict over no plugins is not a verdict.");
            return 1;
        }

        var full = Path.GetFullPath(app);
        ModulePlatformSurface surface;
        try
        {
            surface = host switch
            {
                "container" => PublishedHostSurface.Read(full,
                    ContainerReferenceSet.Read(full, trustedPlatformAssemblies: string.Empty, sharedFrameworksRoot: shared).AssemblyPaths),
                "directory" => DirectorySurface(full, shared),
                _ => throw new ArgumentException($"--host must be 'container' or 'directory', got '{host}'"),
            };
        }
        catch (Exception ex) when (ex is ContainerReferenceSet.UnreadableContainerException or InvalidDataException or ArgumentException or IOException)
        {
            Console.Error.WriteLine($"platform-link: the candidate platform at '{full}' is not readable — {ex.Message}");
            return 1;
        }

        var options = ModuleLinkOptions.WithMembers;
        if (judgeFrom.Count > 0)
            options = options with
            {
                JudgedAssemblies = judgeFrom
                    .SelectMany(d => Directory.GetFiles(d, "*.dll"))
                    .Select(Path.GetFileNameWithoutExtension)
                    .OfType<string>()
                    .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
            };

        var work = Path.Combine(Path.GetTempPath(), "mw-platform-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var results = CheckBundles(bundles, surface, options, work);
            var (headEpoch, breaks) = ReadDeclaration(declaration);
            foreach (var result in results)
                Console.WriteLine($"{(result.Verdict.MayLoad ? "OK  " : "RED ")} {result.Bundle} :: {result.Verdict.Report()}");
            var decision = Decide(results, baseEpoch, headEpoch, breaks);
            foreach (var finding in decision.Findings)
                Console.WriteLine((decision.Green ? "::notice::" : "::error::") + finding);
            var members = results.Sum(r => r.Verdict.CheckedMemberReferences);
            Console.WriteLine($"platform-link: {results.Count} module assembly/assemblies from {bundles.Count} bundle(s); "
                              + $"{results.Sum(r => r.Verdict.CheckedTypeReferences)} type and {members} member reference(s) checked; "
                              + $"epoch {baseEpoch?.ToString() ?? "?"} → {headEpoch?.ToString() ?? "?"}; verdict {(decision.Green ? "GREEN" : "RED")}");
            if (decision.Green && members == 0)
            {
                Console.WriteLine("::error::platform-link: zero member references were checked across the whole plugin set — the member half measured nothing, so GREEN would be vacuous.");
                return 1;
            }
            return decision.Green ? 0 : 1;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (IOException) { /* temp cleanup is the OS's problem, never a verdict */ }
        }
    }
}
