using System.Text.Json;

namespace MeshWeaver.Plugin.Build;

/// <summary>
/// Derives a module's PRIVATE dependency closure from its own <c>&lt;Module&gt;.deps.json</c> —
/// the assemblies the module needs at runtime that the PLATFORM does not carry.
///
/// <para><b>Why this exists.</b> When a module's source left the platform repo (#1882), its
/// package dependencies left the app closure with it — and the bundle lane packed the entry DLL
/// alone, so every landed module faulted at first use on whichever private dependency boot touched
/// first (<c>Microsoft.Extensions.AI.OpenAI</c> on chat, <c>Microsoft.Graph</c> at host start,
/// 2026-08-19/20 memex outage). Hand-naming each dependency with <c>--with</c> is whack-a-mole
/// across 12 modules; pinning the dependency into the platform instead (#1912) ships 43 MB of
/// Graph/Kiota to deployments that send no mail — the exact cost the module split exists to avoid.
/// The bundle must carry the module's OWN dependencies, derived, not remembered.</para>
///
/// <para><b>The rule.</b> The bundle carries the transitive closure of the module's OWN package
/// references — walked over the deps.json graph, stopping at (and never bundling) MeshWeaver.*
/// nodes: those are the platform, they ship in the consumer's <c>/app</c> by construction, and
/// landing one beside the module would shadow the platform's own binary.</para>
///
/// <para><b>A diamond RIDES — deliberately.</b> A package reachable from the module's own
/// references AND from its MeshWeaver.* references (Azure.Identity, Microsoft.Extensions.Options)
/// is bundled anyway: the default load context resolves <c>/app</c>'s copy first whenever the
/// platform carries one (same behaviour as today, at the cost of bundle bytes), and the module's
/// copy takes over the moment the platform stops carrying it. Excluding diamonds instead would
/// couple every landed bundle to the platform's transitive dependency whims — shedding a
/// dependency from the platform (the very point of the module split) would silently break every
/// landed module that relied on it until re-packed. Versions agree while both carry one, because
/// platform and modules restore from the same central package pins.</para>
///
/// <para>🚨 <b>"Those are the platform" is not one answer — it is one answer PER HOST, and the
/// hosts disagree (#3221).</b> The rule above rests on <c>MeshWeaver.*</c> meaning "the consumer's
/// <c>/app</c> carries it". Measured 2026-09-09, two hosts that both load these bundles are built
/// from DIFFERENT REPOSITORIES and carry different sets:</para>
///
/// <list type="bullet">
/// <item><b>the portal</b> (<c>memex-portal-ai</c>) is built from MeshWeaver.Plugins'
/// <c>Memex.Portal.*</c>, alongside <c>src/MeshWeaver.Blazor*</c> and
/// <c>src/MeshWeaver.ContentCollections.Indexing.*</c> — it carries both;</item>
/// <item><b>the gate tester</b> (<c>mw-plugin-test</c>) is built from core's
/// <c>tools/MeshWeaver.PluginTester</c>, whose whole closure is seven core projects, none reaching
/// Blazor — and neither assembly EXISTS in core at all, so it can never carry either.</item>
/// </list>
///
/// <para>MeshWeaver.Plugins' <c>src/platform-shipped.txt</c> — the declared roster feeding
/// <c>ownedPlatformNames</c> — was measured against the PORTAL (its own comments cite images
/// ci.7755/ci.7794 and the three <c>Memex.Portal.*</c> closures). It is therefore right for the
/// portal and wrong for the tester, and no re-measurement of a single image reconciles them: on
/// 2026-09-04..09 <c>MeshWeaver.AI</c> and <c>MeshWeaver.Markdown.Collaboration</c> both loaded as
/// <c>IncompatibleModule … CONTRIBUTING NOTHING</c> on the tester, requiring
/// <c>ContentCollections.Indexing.ChunkPosition</c> and <c>Blazor.BlazorView`2</c> — assemblies this
/// walk had excluded as "platform". That cascaded to 36 refused package installs and reddened a
/// satellite's nightly for six consecutive nights.</para>
///
/// <para>🚨 <b>Do not "fix" it by deleting those two roster lines.</b> That makes the tester work
/// and reintroduces on the PORTAL the same-identity duplicate the roster exists to prevent (#3732).
/// The diamond paragraph above already argues the opposite trade for non-<c>MeshWeaver.*</c>
/// packages — <i>"shedding a dependency from the platform … would silently break every landed
/// module that relied on it"</i> — which is precisely what happened here, one assembly-name
/// convention over. Reconciling the two is a decision between real costs (a per-host roster, or
/// carrying the assembly and letting <c>/app</c> win as the diamond rule does), not a line edit;
/// it is tracked on #3221 and #3732.</para>
///
/// <para>Shared-framework assemblies (<c>FrameworkReference</c>) never appear as package nodes in
/// deps.json, so they are excluded by construction.</para>
///
/// <para>Pure text-in/data-out so the derivation is unit-testable with no build output on disk;
/// <see cref="ModulePackCommand"/> resolves the derived file names against the module folder and
/// refuses a missing file (the build must run with <c>CopyLocalLockFileAssemblies=true</c> for the
/// package assemblies to be present).</para>
/// </summary>
public static class DepsClosure
{
    /// <summary>The derived closure: runtime file names to bundle beside the entry DLL, plus the
    /// MeshWeaver.* nodes the walk stopped at (diagnostic — printed so a pack log shows the
    /// split), plus the TEXT of every declared asset the derivation does not carry (the same
    /// findings, structured, are <see cref="Result.Uncarried"/>), plus the PACKAGE UNIVERSE — every runtime file of every non-MeshWeaver package node
    /// in the whole graph. The universe is the packer's lane witness: a folder that materializes
    /// package assets at all (a publish folder, a CopyLocalLockFileAssemblies build) contains SOME
    /// of it, however framework-trimmed the module's own dependencies are — so "none of the
    /// universe present" cleanly means "this folder never had package assets", never "everything
    /// was framework-resolved".</summary>
    public sealed record Result(
        IReadOnlyList<string> Files,
        IReadOnlyList<string> ExcludedPlatformCarried,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<string> PackageUniverse)
    {
        /// <summary>
        /// 🚨 The RID-specific NATIVE payloads the reachable closure declares — the thing the
        /// bundle used to drop with a warning (#4126). An <c>init</c> PROPERTY, deliberately not a
        /// primary-constructor parameter: a parameter replaces the record's constructor signature,
        /// so every assembly already compiled against the 4-parameter one calls a constructor this
        /// build no longer has, and the repo's binary-compatibility gate refuses it.
        ///
        /// <para>Empty is the ordinary case — almost no module declares a native. Non-empty means
        /// the module's own dependency graph says it needs one, and the packer must carry it: the
        /// runtime resolver (<c>ModuleNativeAssets</c>) probes exactly
        /// <c>&lt;moduleDir&gt;/runtimes/&lt;rid&gt;/native/&lt;lib&gt;</c>, so
        /// <see cref="NativeAsset.RelativePath"/> is already the layout it is looking for.</para>
        /// </summary>
        public IReadOnlyList<NativeAsset> Natives { get; init; } = [];

        /// <summary>
        /// 🚨 Every asset the reachable closure DECLARES that this derivation does not carry — the
        /// two shapes a bundle cannot carry by derivation (<see cref="UncarriedKind"/>). Structured
        /// rather than text so the packer can ask the one question that decides the pack: does
        /// something the caller NAMED carry it instead (#4367)? <see cref="Warnings"/> holds the
        /// same findings in words, one <see cref="UncarriedAsset.Describe"/> each, so the two can
        /// never disagree.
        ///
        /// <para>An <c>init</c> property for the same binary-compatibility reason as
        /// <see cref="Natives"/>.</para>
        /// </summary>
        public IReadOnlyList<UncarriedAsset> Uncarried { get; init; } = [];
    }

    /// <summary>The two shapes a module bundle cannot carry BY DERIVATION (#4126, #4367).</summary>
    public enum UncarriedKind
    {
        /// <summary>A RID-specific MANAGED assembly (<c>assetType: "runtime"</c> under
        /// <c>runtimeTargets</c>): the flat closure has one slot per assembly name and no way to
        /// choose a RID at pack time.</summary>
        RidSpecificManaged,

        /// <summary>A native payload declared at a layout the module loader does not probe —
        /// anything but exactly <c>runtimes/&lt;rid&gt;/native/&lt;file&gt;</c>.</summary>
        UnprobedNative,
    }

    /// <summary>
    /// One asset the module's reachable closure declares and the derivation does not carry.
    /// <c>module-pack</c> REFUSES the pack for it unless something the caller named carries it —
    /// <c>--with</c> for either shape, <c>--with-native</c> for a native (#4367).
    /// </summary>
    /// <param name="Kind">Which of the two shapes.</param>
    /// <param name="Package">The package that declares it.</param>
    /// <param name="RelativePath">The deps.json key, <c>/</c>-separated, verbatim.</param>
    /// <param name="Rid">The RID the declaration is for — its <c>rid</c> property, else the
    /// segment after <c>runtimes/</c>; empty when neither states one.</param>
    public sealed record UncarriedAsset(
        UncarriedKind Kind, string Package, string RelativePath, string Rid)
    {
        /// <summary>The declared file's name — the value <c>--with</c> takes, since the flat
        /// module folder is where the file has to sit for that flag to carry it.</summary>
        public string FileName => RelativePath.Split('/')[^1];

        /// <summary>
        /// For an <see cref="UncarriedKind.UnprobedNative"/>: where the loader WOULD find the
        /// same file for the same RID — <c>runtimes/&lt;rid&gt;/native/&lt;file&gt;</c>, the value
        /// <c>--with-native</c> takes. Null for a managed asset (a RID-specific assembly is not a
        /// native and has no probed layout), and for a native whose RID or file name cannot form
        /// that layout (no RID stated, or a traversal segment) — for those, <c>--with</c> is the
        /// only carrier.
        /// </summary>
        public string? ProbedPath
        {
            get
            {
                if (Kind != UncarriedKind.UnprobedNative)
                    return null;
                var probed = $"runtimes/{Rid}/native/{FileName}";
                return IsProbedNativeLayout(probed) ? probed : null;
            }
        }

        /// <summary>
        /// The finding in words — what is not carried, why, and the step that carries it. 🚨 The
        /// step must be one that SATISFIES the pack's refusal, and each one below does: the
        /// refusal is lifted by exactly <c>--with <see cref="FileName"/></c> or, for a native,
        /// <c>--with-native <see cref="ProbedPath"/></c>. Advice that names any other step would
        /// block a module the message claims to unblock.
        /// </summary>
        public string Describe() => Kind switch
        {
            UncarriedKind.RidSpecificManaged =>
                $"'{Package}' declares a RID-specific MANAGED asset the bundle does not carry: "
                + $"{RelativePath}. The module's flat closure has one slot per assembly name and "
                + "no way to choose a RID at pack time, so the pack will not choose one silently. "
                + "State which copy occupies that slot: copy the file into the module folder root "
                + "before packing (or keep the RID-agnostic copy already there, if that is the one "
                + $"the module needs) and name it with --with {FileName} (--with takes a plain file "
                + "name; it refuses a path).",
            _ =>
                $"'{Package}' declares a native asset at '{RelativePath}', which is NOT the layout "
                + "the module loader probes (exactly runtimes/<rid>/native/<file>) — it is not "
                + "carried, because bytes at a path nothing looks at read as shipped and behave "
                + "as absent. Carry it where the loader looks: "
                + (ProbedPath is { } probed
                    ? $"lay it out at {probed} and name it with --with-native {probed}, or "
                    : "")
                + $"copy it to the module folder root and name it with --with {FileName} (the "
                + "loader's LAST probe is that flat folder).",
        };
    }

    /// <summary>
    /// One loadable native payload a package declares for one RID, as deps.json names it.
    /// </summary>
    /// <param name="RelativePath">The deps.json key, <c>/</c>-separated — always of the shape
    /// <c>runtimes/&lt;rid&gt;/native/&lt;file&gt;</c>, which is the layout the loader probes.</param>
    /// <param name="Rid">The runtime identifier the payload is for.</param>
    /// <param name="Package">The package that declares it — for the pack log, so a native can be
    /// traced back to the dependency that asked for it.</param>
    public sealed record NativeAsset(string RelativePath, string Rid, string Package);

    /// <summary>
    /// The ONE layout a carried native can be found at — <c>NuGetPackageWriter.IsModuleNativeLayout</c> owns the
    /// spelling so the derivation, the packer and the reader cannot drift. <c>ModuleNativeAssets</c>
    /// composes its probe from exactly <c>runtimes/&lt;rid&gt;/native/&lt;lib&gt;</c> and has no
    /// recursive walk, so anything else is not carried but NAMED: bytes at a path the loader never
    /// looks at read as "the bundle ships it" and behave as if it does not.
    ///
    /// <para>🚨 It must be the EXACT four segments, not a <c>/native/</c> substring: a substring
    /// test accepts <c>runtimes/&lt;rid&gt;/other/native/x.so</c> (carried, never probed) and — worse
    /// — <c>runtimes/../../native/x.so</c>, which the PACKER would resolve against the module
    /// directory and read from outside it, before any reader could refuse the bundle.</para>
    /// </summary>
    private static bool IsProbedNativeLayout(string path) =>
        MeshWeaver.Plugin.Packaging.NuGetPackageWriter.IsModuleNativeLayout(path);

    private sealed record Node(
        string Name,
        List<string> Dependencies,
        List<string> RuntimeFiles,
        IReadOnlyList<NativeAsset> Natives,
        IReadOnlyList<(string Path, string Rid)> RidSpecificManaged,
        IReadOnlyList<(string Path, string Rid)> UnreachableNatives,
        string? LibraryType);

    /// <summary>
    /// Derives the private closure from the deps.json text of <paramref name="moduleName"/>.
    /// Throws <see cref="InvalidDataException"/> on a deps.json that lacks the module's own node —
    /// a derivation from the wrong file must be a refusal, never an empty closure that packs a
    /// module which faults at first use.
    /// </summary>
    public static Result Derive(
        string depsJsonText, string moduleName, ISet<string>? ownedPlatformNames = null)
    {
        using var document = JsonDocument.Parse(depsJsonText);
        var root = document.RootElement;

        // The target section: prefer the runtimeTarget's own name (present in every SDK-emitted
        // deps.json), else the first target — never a RID-agnostic guess.
        string? targetName = null;
        if (root.TryGetProperty("runtimeTarget", out var runtimeTarget)
            && runtimeTarget.TryGetProperty("name", out var rtName))
            targetName = rtName.GetString();
        if (!root.TryGetProperty("targets", out var targets))
            throw new InvalidDataException("deps.json has no 'targets' section");
        JsonElement target = default;
        var found = false;
        foreach (var candidate in targets.EnumerateObject())
        {
            if (targetName is null || candidate.Name == targetName)
            {
                target = candidate.Value;
                found = true;
                break;
            }
        }
        if (!found)
            throw new InvalidDataException(
                $"deps.json target '{targetName}' not found among its targets");

        // libraries: name/version -> { type: package|project|... } — the node kind.
        var libraryTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("libraries", out var libraries))
            foreach (var lib in libraries.EnumerateObject())
                if (lib.Value.TryGetProperty("type", out var type))
                    libraryTypes[NameOf(lib.Name)] = type.GetString() ?? "";

        var nodes = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in target.EnumerateObject())
        {
            var name = NameOf(entry.Name);
            var dependencies = new List<string>();
            if (entry.Value.TryGetProperty("dependencies", out var deps))
                dependencies.AddRange(deps.EnumerateObject().Select(d => d.Name));
            var runtimeFiles = new List<string>();
            if (entry.Value.TryGetProperty("runtime", out var runtime))
                runtimeFiles.AddRange(runtime.EnumerateObject()
                    .Select(r => Path.GetFileName(r.Name.Replace('\\', '/')))
                    .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)));
            nodes[name] = new Node(name, dependencies, runtimeFiles, NativesOf(entry.Value, name),
                RidSpecificManagedOf(entry.Value), UnreachableNativesOf(entry.Value),
                libraryTypes.GetValueOrDefault(name));
        }

        if (!nodes.TryGetValue(moduleName, out var module))
            throw new InvalidDataException(
                $"deps.json carries no node for '{moduleName}' — wrong file, or the module was "
                + "renamed after the build");

        // The walk from the module's OWN direct references — MeshWeaver.* references root the
        // platform side and are not walked: their transitives are /app's business, and dragging
        // them in would ship the platform inside the module. EXCEPT the module-OWNED ones
        // (<paramref name="ownedPlatformNames"/> — MeshWeaver.* projects that moved out of the
        // platform and live in the module's own repo, e.g. Import's DataSetReader family): those
        // are nowhere in /app, so stopping at them ships a module that faults on its first
        // sibling — they ride the bundle and their subtrees are walked like any private dep.
        bool IsStop(string d) => IsPlatform(d) && !(ownedPlatformNames?.Contains(NameOf(d)) ?? false);
        var ownRoots = module.Dependencies.Where(d => !IsStop(d)).ToList();
        var (ownReachable, platformStops) = ReachStoppingAtPlatform(nodes, ownRoots, IsStop);
        platformStops.UnionWith(module.Dependencies.Where(IsStop));

        var files = new List<string>();
        var natives = new List<NativeAsset>();
        var uncarried = new List<UncarriedAsset>();
        foreach (var name in ownReachable.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            // 🚨 NO `RuntimeFiles.Count == 0` SHORT-CIRCUIT. It used to sit here, and it made the
            // one case that matters most completely silent: a package whose ONLY contribution is
            // native (SQLitePCLRaw.lib.e_sqlite3 is the measured example) has no runtime file, so
            // it was `continue`d before the native branch ran and warned about NOTHING. The
            // warning existed and could not fire for a pure-native dependency (#4126).
            if (!nodes.TryGetValue(name, out var node))
                continue;
            files.AddRange(node.RuntimeFiles);
            natives.AddRange(node.Natives);
            // 🚨 What the derivation still does NOT carry, now that the probed natives are: a
            // RID-specific MANAGED assembly (`assetType: "runtime"` under runtimeTargets — the flat
            // closure carries one file per name, and there is no flat answer to "which RID's
            // copy"), and a native at a layout the loader never probes. Both are NAMED, as data:
            // module-pack REFUSES the pack for each one unless something the caller named carries
            // it (#4367), so the finding has to say exactly which step lifts the refusal — see
            // UncarriedAsset.Describe. `--with` accepts a plain file name inside the module folder
            // and REFUSES any path component, so the copy has to be flattened into the module
            // folder first, and saying so is the difference between advice and a dead end.
            foreach (var (path, rid) in node.RidSpecificManaged)
                uncarried.Add(new UncarriedAsset(UncarriedKind.RidSpecificManaged, name, path, rid));
            foreach (var (path, rid) in node.UnreachableNatives)
                uncarried.Add(new UncarriedAsset(UncarriedKind.UnprobedNative, name, path, rid));
        }
        var excluded = platformStops.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

        var universe = nodes.Values
            .Where(n => !IsPlatform(n.Name)
                        && string.Equals(n.LibraryType, "package", StringComparison.OrdinalIgnoreCase))
            .SelectMany(n => n.RuntimeFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 🚨 ORDINAL, not OrdinalIgnoreCase. A native's identity is its FILE NAME on a
        // case-sensitive filesystem, and `libFoo.so` and `libfoo.so` are two distinct loadable
        // libraries on Linux — collapsing them would silently drop one, which is the failure mode
        // this whole section exists to end. Managed assembly names elsewhere in this file are
        // case-insensitive because assembly binding is; native paths are not.
        var carried = natives
            .DistinctBy(n => n.RelativePath, StringComparer.Ordinal)
            .OrderBy(n => n.RelativePath, StringComparer.Ordinal)
            .ToList();

        return new Result(
            files.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            excluded,
            [.. uncarried.Select(u => u.Describe())],
            universe)
        {
            Natives = carried,
            Uncarried = uncarried,
        };
    }

    /// <summary>
    /// The LOADABLE native payloads one deps.json node declares, by RID.
    ///
    /// <para>Only <c>assetType: "native"</c> entries: a <c>runtimeTargets</c> section also carries
    /// RID-specific MANAGED assemblies (<c>assetType: "runtime"</c>), and those belong to the flat
    /// closure's own rules, not here. Static libraries (<c>.a</c>, <c>.lib</c>) are excluded — the
    /// same exclusion the in-image lane has applied since #1728: they are link-time inputs, never
    /// loaded, and carrying them would inflate every bundle that touches a native package.</para>
    ///
    /// <para>The key is taken VERBATIM (only <c>\</c> normalised to <c>/</c>) because it already
    /// is the layout the runtime resolver probes. Anything not of the shape
    /// <c>runtimes/&lt;rid&gt;/native/…</c> is skipped rather than reshaped: guessing a layout for
    /// an entry whose own declaration does not state one is how a native ends up somewhere the
    /// loader will never look.</para>
    /// </summary>
    private static IReadOnlyList<NativeAsset> NativesOf(JsonElement node, string package)
    {
        if (!node.TryGetProperty("runtimeTargets", out var targets)
            || targets.ValueKind != JsonValueKind.Object)
            return [];
        var found = new List<NativeAsset>();
        foreach (var entry in targets.EnumerateObject())
        {
            if (!entry.Value.TryGetProperty("assetType", out var assetType)
                || !string.Equals(assetType.GetString(), "native", StringComparison.OrdinalIgnoreCase))
                continue;
            var path = entry.Name.Replace('\\', '/');
            if (!IsProbedNativeLayout(path))
                continue;               // reported by UnreachableNativesOf, never silently dropped
            if (path.EndsWith(".a", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".lib", StringComparison.OrdinalIgnoreCase))
                continue;
            found.Add(new NativeAsset(path, RidOf(entry.Value, path), package));
        }
        return found;
    }

    /// <summary>
    /// Native assets a node declares at a layout the loader does NOT probe — reported, not carried.
    /// Static libraries are excluded here too: they are link-time inputs, and naming one as
    /// "unreachable" would send a reader looking for a load path that was never wanted.
    /// </summary>
    private static IReadOnlyList<(string Path, string Rid)> UnreachableNativesOf(JsonElement node)
    {
        if (!node.TryGetProperty("runtimeTargets", out var targets)
            || targets.ValueKind != JsonValueKind.Object)
            return [];
        var found = new List<(string Path, string Rid)>();
        foreach (var entry in targets.EnumerateObject())
        {
            if (!entry.Value.TryGetProperty("assetType", out var assetType)
                || !string.Equals(assetType.GetString(), "native", StringComparison.OrdinalIgnoreCase))
                continue;
            var path = entry.Name.Replace('\\', '/');
            if (IsProbedNativeLayout(path)
                || path.EndsWith(".a", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".lib", StringComparison.OrdinalIgnoreCase))
                continue;
            found.Add((path, RidOf(entry.Value, path)));
        }
        return found;
    }

    /// <summary>
    /// The RID-specific MANAGED assets a node declares (<c>assetType: "runtime"</c> under
    /// <c>runtimeTargets</c>) — reported, not carried. See the warning at the call site.
    /// </summary>
    private static IReadOnlyList<(string Path, string Rid)> RidSpecificManagedOf(JsonElement node)
    {
        if (!node.TryGetProperty("runtimeTargets", out var targets)
            || targets.ValueKind != JsonValueKind.Object)
            return [];
        var found = new List<(string Path, string Rid)>();
        foreach (var entry in targets.EnumerateObject())
            if (entry.Value.TryGetProperty("assetType", out var assetType)
                && string.Equals(assetType.GetString(), "runtime", StringComparison.OrdinalIgnoreCase))
            {
                var path = entry.Name.Replace('\\', '/');
                found.Add((path, RidOf(entry.Value, path)));
            }
        return found;
    }

    /// <summary>The RID a <c>runtimeTargets</c> entry is for: its <c>rid</c> property, else the
    /// segment after <c>runtimes/</c> in its key, else empty. Only a <c>runtimes/</c> key names a
    /// RID by position — reading the second segment of any other key would mint one from a folder
    /// name, and <see cref="UncarriedAsset.ProbedPath"/> would then advise a slot no host
    /// probes.</summary>
    private static string RidOf(JsonElement entry, string path)
    {
        var rid = entry.TryGetProperty("rid", out var r) ? r.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(rid))
            rid = path.Split('/') is ["runtimes", var fromPath, ..] ? fromPath : "";
        return rid;
    }

    // ONE spelling of "the platform side owns this name", shared with the platform-shipped witness
    // and with PublishedBundleCatalogue's sealed-set reading (#3732) — the three used to carry
    // three copies of the same three lines.
    private static bool IsPlatform(string name) =>
        MeshWeaver.Compiler.PlatformShippedAssemblies.IsPlatformAssemblyName(name);

    /// <summary>Transitive reachability over the dependency edges, from <paramref name="roots"/>
    /// inclusive — STOPPING at MeshWeaver.* nodes (collected separately, never walked: their
    /// transitives are the platform's). Missing nodes (framework-supplied names deps.json lists as
    /// dependencies but carries no entry for) are simply absent — nothing to bundle, nothing to
    /// walk.</summary>
    private static (HashSet<string> Reached, HashSet<string> PlatformStops) ReachStoppingAtPlatform(
        IReadOnlyDictionary<string, Node> nodes, IEnumerable<string> roots,
        Func<string, bool> isStop)
    {
        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(roots);
        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (isStop(name))
            {
                stops.Add(name);
                continue;
            }
            if (!reached.Add(name) || !nodes.TryGetValue(name, out var node))
                continue;
            foreach (var dependency in node.Dependencies)
                queue.Enqueue(dependency);
        }
        return (reached, stops);
    }

    /// <summary>"Name/version" → "Name" (deps.json keys carry the resolved version).</summary>
    private static string NameOf(string key)
    {
        var slash = key.IndexOf('/');
        return slash < 0 ? key : key[..slash];
    }
}
