using System.Collections.Immutable;
using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// One module pack as the boot loader is about to load it: WHERE the bytes come from, WHICH bytes
/// they are, and whether a different copy in the module store was passed over.
/// </summary>
/// <param name="Name">The module's simple name.</param>
/// <param name="Source">
/// <see cref="ModuleActivationSources.AppSettings"/> or <see cref="ModuleActivationSources.Store"/> —
/// the lane the entry came from, which is what decides the resolution rule applied to it.
/// </param>
/// <param name="Path">The EXACT path handed to <c>InstallAssemblies</c>. Not a re-derivation.</param>
/// <param name="Mvid">The assembly's ModuleVersionId, or <c>null</c> when the file could not be read.</param>
/// <param name="WrittenUtc">The file's last-write time, or <c>null</c> when it could not be read.</param>
/// <param name="Shadowed">
/// A copy of the SAME module in the store that is newer and carries DIFFERENT bytes, or <c>null</c>
/// when nothing was passed over. Present means the running portal is serving an older pack than the
/// one on the volume — #2223.
/// </param>
public sealed record ModuleLoadLine(
    string Name,
    string Source,
    string Path,
    Guid? Mvid,
    DateTimeOffset? WrittenUtc,
    ModuleCopy? Shadowed);

/// <summary>One copy of a module's entry assembly found on disk.</summary>
/// <param name="Path">Full path of the DLL.</param>
/// <param name="Mvid">Its ModuleVersionId, or <c>null</c> when unreadable.</param>
/// <param name="WrittenUtc">Its last-write time, or <c>null</c> when unreadable.</param>
public sealed record ModuleCopy(string Path, Guid? Mvid, DateTimeOffset? WrittenUtc);

/// <summary>
/// 🚨 <b>SAY WHICH COPY OF EACH MODULE PACK IS ACTUALLY LOADED — because nothing did, and a fix
/// could ship end-to-end without ever running.</b>
///
/// <para>Measured on memex-cloud 2026-08-25 (#2223): the portal ran an image built from the merge
/// commit of the fix, the module store held two newer copies of <c>MeshWeaver.Blazor.Views</c> that
/// both contained it, and <c>/proc/1/maps</c> showed the process had memory-mapped the IMAGE's copy —
/// which did not. Every lane was green. The only evidence the new code was not live existed in
/// production, on a pod, in a file nobody reads.</para>
///
/// <para><b>Why the resolution is right and the silence was the bug.</b> A BASELINE
/// (<c>Modules:Assemblies</c>) entry resolves through <c>MeshBuilder.ResolveModulePath</c>, whose
/// probes are landed root → image → app closure. Landing writes GENERATIONS
/// (<c>modules/&lt;name&gt;@&lt;id&gt;/</c>) and records the pointer in the sidecar, so the landed
/// probe — which looks in the fixed <c>modules/&lt;name&gt;/</c> — misses, and the image copy wins.
/// The sidecar entry that WOULD have named the generation is deduped away by name, silently, because
/// the baseline already claimed it (<c>ComputeEffectiveModuleEntries</c>). Every step is deliberate;
/// what was missing is anyone saying so out loud.</para>
///
/// <para>🚨 <b>It WARNS. It never refuses to start.</b> A pod that will not boot cannot be given the
/// fix for whatever is wrong with it — the same deadlock as a registry that cannot start delivering
/// the module that breaks it (#2234). Which copy SHOULD win is a policy question this does not
/// answer; making the answer visible is the whole deliverable.</para>
///
/// <para><b>Identity, not just a timestamp.</b> Two copies of a pack with the same MVID are the same
/// bytes in two places — that is not a defect and must not warn, or the line becomes noise everyone
/// scrolls past. The warning fires only on a store copy that is BOTH newer AND different.</para>
/// </summary>
public static class ModuleLoadReport
{
    /// <summary>The prefix every line carries, so one grep finds the whole report.</summary>
    public const string LogPrefix = "[ModuleLoad]";

    /// <summary>
    /// Describe what boot is about to load. Pure with respect to the caller's decision: it reports
    /// the paths it is GIVEN — the same array that goes to <c>InstallAssemblies</c> — so the report
    /// and the load can never disagree by construction.
    /// </summary>
    /// <param name="moduleRoot">The writable module root (<see cref="ModuleRoot.Resolve(string?)"/>).</param>
    /// <param name="resolved">Each effective module and the exact path it resolved to.</param>
    public static ImmutableList<ModuleLoadLine> Describe(
        string moduleRoot, IEnumerable<(EffectiveModule Module, string Path)> resolved)
    {
        var lines = ImmutableList.CreateBuilder<ModuleLoadLine>();
        foreach (var (module, path) in resolved)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(module.Entry);
            var loaded = Describe(path);
            lines.Add(new ModuleLoadLine(
                name,
                module.Landed is null ? ModuleActivationSources.AppSettings : ModuleActivationSources.Store,
                path,
                loaded.Mvid,
                loaded.WrittenUtc,
                NewerDifferentStoreCopy(moduleRoot, name, loaded)));
        }
        return lines.ToImmutable();
    }

    /// <summary>
    /// Render the report: one <paramref name="info"/> line per module, plus a <paramref name="warn"/>
    /// line for every pack whose store copy was passed over. Boot is pre-DI, so production passes
    /// <c>Console.WriteLine</c> / <c>Console.Error.WriteLine</c> (pod stdout/stderr ship to Loki
    /// regardless).
    /// </summary>
    public static void Write(
        IEnumerable<ModuleLoadLine> lines, Action<string> info, Action<string> warn)
    {
        foreach (var line in lines)
        {
            info($"{LogPrefix} {line.Name} ← {line.Path} "
                 + $"(source={line.Source}, mvid={Format(line.Mvid)}, written={Format(line.WrittenUtc)})");
            if (line.Shadowed is { } shadowed)
                warn($"{LogPrefix} STALE PACK: {line.Name} is loading {line.Path} "
                     + $"(mvid={Format(line.Mvid)}, written={Format(line.WrittenUtc)}) while the module "
                     + $"store holds a NEWER, DIFFERENT copy at {shadowed.Path} "
                     + $"(mvid={Format(shadowed.Mvid)}, written={Format(shadowed.WrittenUtc)}). "
                     + "This portal is serving the older pack — a fix that merged, built and landed can "
                     + $"be invisible here. {Remediation(line.Source)}");
        }
    }

    /// <summary>
    /// What to actually DO about a shadowed pack — which differs by the lane the entry came from,
    /// because the two lanes are shadowed for different reasons.
    ///
    /// <para>🚨 One remediation for both would be wrong for one of them, and a warning that names
    /// the wrong fix is worse than one that names none: a store-installed module is not listed in
    /// <c>Modules:Assemblies</c> at all, so telling an operator to delist it sends them looking for
    /// a line that does not exist.</para>
    /// </summary>
    private static string Remediation(string source) =>
        string.Equals(source, ModuleActivationSources.Store, StringComparison.Ordinal)
            // The sidecar's Directory pointer names the generation to load. A newer generation on
            // disk means landing wrote the bytes but this entry is still pointing at the previous
            // one — the pointer write is the half that did not land.
            ? "This entry is store-installed, so its activation.json Directory pointer — not "
              + "Modules:Assemblies — decides which generation loads, and it still names the older "
              + "one. Re-install the module so activation records the newer generation."
            // The baseline lane: ResolveModulePath's landed probe looks in the fixed
            // modules/<Name>/ folder, which generation landing never writes, so the image copy wins.
            // Before #2548 the sidecar entry that WOULD have named the generation was also deduped
            // away by name; it no longer is, so reaching this branch means no usable store entry
            // exists — which changes the advice completely.
            // 🚨 NOT "delist it". Since #2548 a USABLE store entry overrides a same-named baseline,
            // so a baseline entry that is still winning means there is no usable store entry to
            // take over — and delisting would remove the only copy that loads rather than promoting
            // a newer one. The baseline is the floor, and the floor is what you keep.
            : "A baseline Modules:Assemblies entry is loading the image copy because no usable "
              + "store-installed entry claims this name — an enabled entry whose landed DLL exists "
              + "would override it. Re-install the module so activation records a landed generation "
              + "this instance can load; do NOT delist the baseline, which is the fallback that "
              + "keeps the module loading at all.";

    /// <summary>
    /// The newest copy of <paramref name="name"/> under <c>{moduleRoot}/modules/</c> that is neither
    /// the loaded file nor byte-identical to it, and is newer than it. Null when the store holds
    /// nothing that was passed over.
    ///
    /// <para>Every failure mode answers "nothing was shadowed": an unreadable directory, an
    /// unreadable DLL, an unknown MVID on either side. A report that cannot see clearly must not
    /// invent a warning — an operator who learns to ignore this line has lost the whole signal.</para>
    /// </summary>
    private static ModuleCopy? NewerDifferentStoreCopy(string moduleRoot, string name, ModuleCopy loaded)
    {
        if (loaded.WrittenUtc is not { } loadedAt)
            return null;
        try
        {
            var modulesRoot = System.IO.Path.Combine(moduleRoot, "modules");
            if (!Directory.Exists(modulesRoot))
                return null;
            return Directory.EnumerateDirectories(modulesRoot)
                // Landing writes modules/<name>@<generation>/; the legacy fixed folder is
                // modules/<name>/. Both are this module's, nothing else is.
                .Where(d => IsDirectoryFor(System.IO.Path.GetFileName(d), name))
                .Select(d => System.IO.Path.Combine(d, name + ".dll"))
                .Where(File.Exists)
                .Where(p => !string.Equals(p, loaded.Path, StringComparison.Ordinal))
                .Select(Describe)
                .Where(c => c.WrittenUtc > loadedAt)
                // Same MVID = the same bytes in a second place, which is normal and not a defect.
                // An unknown MVID on either side is "cannot tell", which reports nothing.
                .Where(c => c.Mvid is not null && loaded.Mvid is not null && c.Mvid != loaded.Mvid)
                .MaxBy(c => c.WrittenUtc);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsDirectoryFor(string directoryName, string moduleName) =>
        string.Equals(directoryName, moduleName, StringComparison.OrdinalIgnoreCase)
        || (directoryName.Length > moduleName.Length
            && directoryName[moduleName.Length] == '@'
            && directoryName.AsSpan(0, moduleName.Length)
                .Equals(moduleName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// One file's identity. Reading the MVID out of the PE metadata is deliberately NOT
    /// <c>Assembly.LoadFrom</c>: this runs before anything is loaded, and loading a file just to
    /// describe it would pin the very assembly the report may be telling you not to trust.
    /// </summary>
    private static ModuleCopy Describe(string path)
    {
        Guid? mvid = null;
        DateTimeOffset? writtenUtc = null;
        try
        {
            writtenUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (pe.HasMetadata)
            {
                var metadata = pe.GetMetadataReader();
                mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
            }
        }
        catch (Exception)
        {
            // Unreadable is reported as unknown, never as a fault: the report must not be able to
            // stop a boot it exists to explain.
        }
        return new ModuleCopy(path, mvid, writtenUtc);
    }

    private static string Format(Guid? mvid) =>
        mvid is { } value ? value.ToString("N")[..8] : "unknown";

    private static string Format(DateTimeOffset? at) =>
        at is { } value ? value.ToString("u", CultureInfo.InvariantCulture) : "unknown";
}
