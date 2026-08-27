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
                + "replaced at a free index.");

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
        var required = configuration.GetSection(RequiredKey).GetChildren().Select(child => child.Value);
        return [.. required
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Where(entry => !exists(resolve(entry!)))
            .Select(entry => entry!)];
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
