using Microsoft.Extensions.Configuration;

namespace MeshWeaver.Mesh;

/// <summary>
/// Installs the modules a host's configuration lists — the <c>Modules:Assemblies</c> lane, which is
/// how a module gets to RUN once the publish has put its bits under <c>modules/&lt;Name&gt;/</c>.
/// </summary>
public static class MeshBuilderModuleActivation
{
    /// <summary>The configuration key every host reads its module baseline from.</summary>
    public const string AssembliesKey = "Modules:Assemblies";

    /// <summary>
    /// The modules this deployment cannot correctly serve without — the loud half of
    /// <see cref="AssembliesKey"/>.
    ///
    /// <para>🚨 <b>Why this exists.</b> A listed-but-absent module is SKIPPED, deliberately: a host
    /// that will not start is worse than one missing a feature, and that rule is what stopped the
    /// 3.0.0-rc5 boot loop. But the same silence is a trap once modules START LEAVING THE IMAGE. Ship
    /// a build whose image no longer carries a pack, land it on an instance that never installed the
    /// package, and the feature simply is not there — charts blank, maps blank, voice mute — behind
    /// one stderr line and a green rollout. Nothing fails, so nothing is noticed.</para>
    ///
    /// <para>Naming a module here says "absent is a FAULT here". It does not change boot: the host
    /// still starts (see above). It changes VISIBILITY — the host reports the absence, and a
    /// readiness probe wired to it stalls the rollout so the pods that still have the module keep
    /// serving.</para>
    /// </summary>
    public const string RequiredKey = "Modules:Required";

    /// <summary>
    /// The key a deployment states with that its OWN <see cref="RequiredKey"/> entries are the
    /// COMPLETE required set for this instance — the image's own list does not apply.
    ///
    /// <para>🚨 <b>Why a second key is needed at all.</b> <c>Modules:Required</c> is an ARRAY, and
    /// configuration merges arrays BY INDEX: a later provider replaces the entries it names and
    /// leaves every other index of the earlier one standing. An array can therefore express
    /// "replace entry N" and can never express "these and only these" — so a deployment list
    /// SHORTER than the image's requires the image's tail it never named, and an EMPTY list
    /// requires the image's list in full. Emptying a record's <c>requiredModules</c> does not
    /// relax the requirement, it restores it (#4476).</para>
    ///
    /// <para>The claim is read off provider ORDER, the same mechanism <see cref="ShadowedRequired"/>
    /// uses: from the provider that states it onwards — the ConfigMap / container environment a
    /// <c>Deployments/&lt;name&gt;</c> record renders, and anything layered after it — the entries
    /// supplied THERE are the whole requirement. No count, no padding, and no knowledge of how long
    /// the image's list is, which is the coupling the key exists to remove. A later provider may
    /// withdraw the claim by setting it to <c>false</c>.</para>
    ///
    /// <para>🚨 <b>Opt-in, deliberately.</b> The image's list is the PLATFORM's floor — the AI
    /// engine, the chat renderer and the collaboration pack are each named there because losing one
    /// silently is a measured outage — and no fleet record states a complete set today (all three
    /// name five against the image's nine). Taking a partial list as authoritative BY DEFAULT would
    /// have un-required four modules on every instance the day it shipped. Where the claim is
    /// absent, nothing changes and <see cref="UnstatedRequired"/> names what the deployment did not
    /// state.</para>
    /// </summary>
    public const string RequiredIsAuthoritativeKey = "Modules:RequiredIsAuthoritative";

    /// <summary>
    /// The required-module entries this deployment ACTUALLY declares — the ONE reading the boot
    /// path and the health check share, so a probe can never disagree with the log line that
    /// preceded it.
    ///
    /// <para>Without <see cref="RequiredIsAuthoritativeKey"/> this is the merged
    /// <see cref="RequiredKey"/> array, blanks dropped — exactly what every caller read before.
    /// With it, the entries the authoritative provider (and anything layered after it) supplies,
    /// and those only: the image's indices are not required here.</para>
    /// </summary>
    /// <param name="configuration">The host configuration. A non-root <see cref="IConfiguration"/>
    /// carries no provider list, so no claim can be read off it and the merged array stands.</param>
    public static string[] RequiredEntries(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var stated = StatedIndices(configuration);
        return [.. configuration.GetSection(RequiredKey).GetChildren()
            .Where(child => stated is null || stated.Contains(child.Key))
            .Select(child => child.Value)
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry!)];
    }

    /// <summary>
    /// The required modules this instance demands that its OWN overlay never named — the quiet half
    /// of the by-index merge, and the half no other check can see.
    ///
    /// <para>🚨 <b>Not the same question as <see cref="ShadowedRequired"/>.</b> That one sees an
    /// entry the deployment REPLACED. This one sees the entries the deployment never REACHED: a
    /// list shorter than the image's leaves the image's tail standing, so the instance requires
    /// modules its record does not name — and nothing is missing, nothing is shadowed, the deploy
    /// succeeds and the record is simply not a description of what the instance requires. Measured
    /// on pearl.meshweaver.cloud, 2026-09-16 (#4476): a record naming five, an image naming six,
    /// and a missing-module report that named the sixth.</para>
    ///
    /// <para>Empty when the deployment states <see cref="RequiredIsAuthoritativeKey"/> (it stated
    /// the whole set, so nothing is unstated) and empty when only ONE provider supplies entries at
    /// all — a Monolith, a test mesh or the CLI layers nothing, and reporting the image's own list
    /// back to it would be noise. The remedy the report names is the claim, never "pick a free
    /// slot": a free slot is a property of an image the deployment cannot read, and the image's
    /// list has already grown from seven entries to nine underneath one.</para>
    /// </summary>
    /// <param name="configuration">The host configuration. A non-root <see cref="IConfiguration"/>
    /// carries no provider list and yields an empty result — the check does not apply.</param>
    public static string[] UnstatedRequired(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration is not IConfigurationRoot root || StatedIndices(configuration) is not null)
            return [];

        // The deployment's own overlay is the LAST provider supplying anything under the key — the
        // ConfigMap / container environment, layered over the image's appsettings. Provider order
        // carries it; no provider-type sniffing, so any layering (files, env, command line) works.
        var providers = root.Providers.ToArray();
        var overlay = -1;
        for (var i = 0; i < providers.Length; i++)
            if (providers[i].GetChildKeys([], RequiredKey).Any())
                overlay = i;
        if (overlay < 0)
            return [];

        var stated = providers[overlay].GetChildKeys([], RequiredKey).ToHashSet(StringComparer.Ordinal);
        return [.. root.GetSection(RequiredKey).GetChildren()
            .Where(child => !stated.Contains(child.Key))
            .Select(child => child.Value)
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry!)];
    }

    /// <summary>
    /// The <see cref="RequiredKey"/> indices an AUTHORITATIVE deployment supplies, or null when no
    /// provider claims <see cref="RequiredIsAuthoritativeKey"/> — in which case every index of the
    /// merged array counts, which is what every caller read before the claim existed.
    /// </summary>
    private static HashSet<string>? StatedIndices(IConfiguration configuration)
    {
        if (configuration is not IConfigurationRoot root)
            return null;

        var providers = root.Providers.ToArray();
        var authority = -1;
        for (var i = 0; i < providers.Length; i++)
            if (providers[i].TryGet(RequiredIsAuthoritativeKey, out var claim))
                authority = bool.TryParse(claim, out var claimed) && claimed ? i : -1;
        if (authority < 0)
            return null;

        var indices = new HashSet<string>(StringComparer.Ordinal);
        for (var i = authority; i < providers.Length; i++)
            foreach (var index in providers[i].GetChildKeys([], RequiredKey))
                indices.Add(index);
        return indices;
    }

    /// <summary>
    /// Resolves each <c>Modules:Assemblies</c> entry through
    /// <see cref="MeshBuilder.ResolveModulePath(string)"/> — one resolver, shared, probing
    /// <c>modules/&lt;name&gt;/</c> before the app closure — and installs what is actually there.
    ///
    /// <para>🚨 <b>A listed-but-absent module is SKIPPED, never fatal.</b>
    /// <see cref="MeshBuilder.InstallAssemblies"/> does <c>Assembly.LoadFrom</c>, which throws
    /// <see cref="FileNotFoundException"/>, so one stale line would take the host down before
    /// anything serves — as happened on 3.0.0-rc5, whose image no longer shipped fourteen extracted
    /// modules while appsettings still listed them. A missing module must surface as a missing
    /// FEATURE, which is diagnosable; a host that will not start is not.</para>
    ///
    /// <para>This is the BASELINE half only: it reads the host's own configuration and the bits its
    /// publish laid down. A deployment that also lets an operator install modules at runtime has a
    /// second source (the activation sidecar, generations, the platform floor) and composes them
    /// first — see the portal's boot, which feeds the union through the same resolver.</para>
    /// </summary>
    /// <param name="builder">The mesh builder to install into.</param>
    /// <param name="configuration">The host configuration carrying <c>Modules:Assemblies</c>.</param>
    /// <param name="onSkip">
    /// Called once per skipped entry with a human-readable reason. Defaults to stderr, which is
    /// right for a pre-DI boot path: there is no logger yet, and stdout/stderr is what gets shipped.
    /// </param>
    /// <returns>The same builder, for chaining.</returns>
    public static MeshBuilder InstallConfiguredModules(
        this MeshBuilder builder, IConfiguration configuration, Action<string>? onSkip = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // GetChildren().Value rather than Get<string[]>(): the binder lives in a separate package
        // and this assembly deliberately takes Configuration.Abstractions only. For a string array
        // the two read identically — the children of Modules:Assemblies ARE the entries.
        var entries = configuration.GetSection(AssembliesKey).GetChildren().Select(child => child.Value);
        var report = onSkip ?? (message => Console.Error.WriteLine($"[ModuleActivation] {message}"));
        var resolved = ResolveInstallable(entries, MeshBuilder.ResolveModulePath, File.Exists, report);

        // The loud half. Absent-and-required is reported at boot as well as by the health check, so
        // it is in the pod log the operator already has open — not only behind a probe endpoint.
        foreach (var missing in MissingRequired(configuration, MeshBuilder.ResolveModulePath, File.Exists))
            report($"REQUIRED module '{missing}' does NOT resolve from this image. This deployment "
                + $"declares it under {RequiredKey}, so whatever it provides is missing. The host is "
                + "starting anyway — a host that will not start is worse. The readiness probe then "
                + "separates the two cases the boot path cannot: a pack this image claims under "
                + $"{AssembliesKey} and lost HOLDS the rollout, while a store-delivered module the "
                + "registry has not landed yet is reported degraded and named (it is not something "
                + "a held rollout could deliver).");

        // The SILENT half of the same key: a requirement that did not go missing but was
        // OVERWRITTEN. Nothing above can see it — the entry is not absent, it was never asked for.
        foreach (var shadowed in ShadowedRequired(configuration))
            report($"REQUIRED module '{shadowed}' was silently REPLACED at its index and is no "
                + $"longer required by this deployment. {RequiredKey} is an ARRAY and an override "
                + "binds BY INDEX: setting one entry does not append to the list, it replaces "
                + $"whatever the image declared at that position. Nothing else reports this — the "
                + "module is simply not required any more, so it is never missing, the deploy "
                + "succeeds and the health check stays green with the guard gone. Move the new "
                + "entry to the first index PAST the image's own list, or restate the entry you "
                + $"replaced at a free index — or state {RequiredIsAuthoritativeKey}=true, after "
                + "which this deployment's own entries ARE the complete set and no index of the "
                + "image's list applies.");

        // The QUIETEST half: a requirement the deployment never reached. Its own list is shorter
        // than the image's, so the image's tail stands — this instance requires a module its record
        // does not name, and nothing above can see it (nothing is missing, nothing was replaced).
        foreach (var unstated in UnstatedRequired(configuration))
            report($"REQUIRED module '{unstated}' comes from the IMAGE, at an index this "
                + $"deployment's own {RequiredKey} list does not reach. {RequiredKey} is an ARRAY "
                + "and an override binds BY INDEX, so a shorter list does not replace a longer one "
                + "— it overwrites the leading entries and leaves the tail standing, and an EMPTY "
                + "list leaves the image's list standing in full. This instance therefore requires "
                + "a module its own declaration never names. State "
                + $"{RequiredIsAuthoritativeKey}=true to make this deployment's entries the "
                + "complete set (including when there are none), or restate this entry in it.");

        // Hand the configuration to the builder BEFORE anything is installed: an attribute's
        // BuilderConfigurations runs inside InstallAssemblies, so a module asking "what did this
        // deployment configure?" must find the answer already there. We hold it either way — not
        // passing it on was the whole of the gap.
        builder.WithConfiguration(configuration);

        return resolved.Length == 0 ? builder : builder.InstallAssemblies(resolved);
    }

    /// <summary>
    /// The required modules this host cannot resolve — empty when the deployment declares none, or
    /// when every declared one is present. Pure and configuration-driven on purpose: the boot path
    /// and the health check ask the SAME question the same way, so a probe can never disagree with
    /// the log line that preceded it.
    /// </summary>
    public static string[] MissingRequired(
        IConfiguration configuration, Func<string, string> resolve, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return [.. RequiredEntries(configuration).Where(entry => !exists(resolve(entry)))];
    }

    /// <summary>
    /// The required modules an override SILENTLY REPLACED — declared by one configuration source
    /// and overwritten at the same index by a later one, with the replaced module named nowhere
    /// else in the effective list.
    ///
    /// <para>🚨 <b>Why this is not the same question as <see cref="MissingRequired"/>, and why
    /// nothing else can ask it.</b> <c>Modules:Required</c> is an ARRAY, so an override binds BY
    /// INDEX: <c>Modules__Required__5</c> replaces whatever the image's own list holds at index 5 —
    /// it does not append a sixth requirement. The failure that follows is invisible from every
    /// direction. The replaced module is not MISSING (nobody asked for it any more), so
    /// <see cref="MissingRequired"/> and the readiness contract both have nothing to say; the
    /// override is not un-rendered, so the chart's coverage gate passes; the deploy succeeds and
    /// <c>/health</c> stays green having quietly stopped guarding a module. Measured on Memex#131:
    /// an overlay added <c>MeshWeaver.Mcp.dll</c> at index 5 to require the MCP endpoint, and index
    /// 5 of the image's list is <c>MeshWeaver.Social.dll</c>.</para>
    ///
    /// <para>The two halves are only ever visible TOGETHER in the running process — the image's
    /// baseline ships in one repository and the overlay lives in another — which is why this is a
    /// boot-time check and not a chart guard. Provider ORDER is the whole mechanism: a value a
    /// provider supplies that is not the effective one has been shadowed by a later provider, so no
    /// provider-type sniffing is needed and any layering (files, env, command line) is covered.</para>
    ///
    /// <para>Two shapes are deliberately NOT reported, because both are how the key is meant to be
    /// used: <b>blanking</b> an entry the deployment cannot satisfy (the effective value is empty —
    /// an explicit "not required here", and the whole remedy the 2026-08-23 rollouts had), and
    /// <b>reordering</b>, where the replaced module still appears at some other index and no
    /// requirement was lost.</para>
    /// </summary>
    /// <param name="configuration">The host configuration. A non-root <see cref="IConfiguration"/>
    /// carries no provider list and yields an empty result — the check simply does not apply.</param>
    public static string[] ShadowedRequired(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration is not IConfigurationRoot root)
            return [];

        // A deployment that states the COMPLETE set has not shadowed anything: replacing the
        // image's leading indices is the whole POINT of the claim, and the image's other indices
        // are not required here at all. Reporting either would make the claim look like a mistake.
        if (StatedIndices(configuration) is not null)
            return [];

        var effective = root.GetSection(RequiredKey).GetChildren()
            .ToDictionary(child => child.Key, child => child.Value, StringComparer.Ordinal);
        var stillRequired = effective.Values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var shadowed = new List<string>();
        foreach (var (index, winner) in effective)
        {
            // A BLANK winner is an explicit "this deployment cannot satisfy it" — the sanctioned
            // way to drop a requirement, and never a mistake to report.
            if (string.IsNullOrWhiteSpace(winner))
                continue;

            foreach (var provider in root.Providers)
            {
                if (!provider.TryGet($"{RequiredKey}:{index}", out var supplied)
                    || string.IsNullOrWhiteSpace(supplied)
                    || string.Equals(supplied, winner, StringComparison.OrdinalIgnoreCase)
                    // Reordered, not lost: the module is still required, at another index.
                    || stillRequired.Contains(supplied))
                    continue;

                shadowed.Add(supplied);
            }
        }

        return [.. shadowed.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The pure half: entries in, installable paths out, one <paramref name="onSkip"/> line per
    /// entry that is not there. Separated from the builder so the skip-don't-crash rule is testable
    /// without a filesystem or a mesh — the rule matters most in exactly the situation where
    /// standing one up is hardest.
    /// </summary>
    public static string[] ResolveInstallable(
        IEnumerable<string?>? entries,
        Func<string, string> resolve,
        Func<string, bool> exists,
        Action<string> onSkip)
    {
        if (entries is null)
            return [];

        var installable = new List<string>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;

            var path = resolve(entry);
            if (exists(path))
            {
                installable.Add(path);
                continue;
            }

            onSkip($"SKIPPED module '{entry}': no assembly at '{path}'. It is listed under "
                + $"{AssembliesKey} but this build does not ship it — delist it, or install it as a "
                + "module. Starting without it; whatever it provided is absent.");
        }

        return [.. installable];
    }
}
