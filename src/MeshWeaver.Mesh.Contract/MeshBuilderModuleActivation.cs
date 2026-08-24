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
            report($"REQUIRED module '{missing}' is NOT present. This deployment declares it under "
                + $"{RequiredKey}, so whatever it provides is missing and that is a fault, not a "
                + "choice. The host is starting anyway — a host that will not start is worse — but a "
                + "readiness probe wired to RequiredModulesAbsent will hold the rollout.");

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
