using System.Collections.Immutable;
using MeshWeaver.AI.Connect;
using MeshWeaver.AI.Portal;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

[assembly: MeshWeaver.AI.AiMeshModule]

namespace MeshWeaver.AI;

/// <summary>
/// The AI engine as a MODULE: listing <c>MeshWeaver.AI.dll</c> under <c>Modules:Assemblies</c>
/// installs the agent runtime, its content types, and the app that maintains them — with no
/// compiled call from any composition root. One assembly, one module, one Store entry (#2276).
///
/// <para>This is what let <c>Memex.Portal.Shared</c> drop its <c>ProjectReference</c>: every
/// registration below used to sit in <c>MemexConfiguration</c>, which meant the portal compiled
/// against the engine in order to configure it. A closure DLL and a registry module of the same
/// name are mutually exclusive (the landing service refuses with a 409 on every instance), so the
/// reference had to go before the engine could ship from the Store at all.</para>
///
/// <para>🚨 It reads <see cref="MeshBuilder.Configuration"/>, which exists for exactly this
/// (#2300): the serve-from-DB decision is taken at BUILDER time — it sets
/// <c>MeshNode.IsDefinitionOnly</c>, an <c>init</c> property — so the options pipeline is too late.
/// A host that supplies no configuration gets the same answer an absent key would give, never a
/// guess: these are decisions whose wrong value is silent.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class AiMeshModuleAttribute : MeshNodeProviderAttribute
{
    /// <inheritdoc />
    public override IEnumerable<Func<MeshBuilder, MeshBuilder>> BuilderConfigurations =>
    [
        builder => builder
            .AddAI(ServeFromPartitions(builder.Configuration))
            // The AI menu's navigation entries, seeded as platform-static UiContribution nodes.
            .AddAiMenuContributions()
            .ConfigureServices(services => AddAiComposition(services, builder.Configuration))
    ];

    /// <summary>
    /// The partitions Postgres serves, from <c>Features:StaticRepoSync:Partitions</c>.
    ///
    /// <para>🚨 AI content is served as a UNIT: if the deployment names ANY AI partition, all of
    /// them are served, so a config that lists only some can never leave one (typically
    /// <c>Skill</c>) in-memory while the rest go to the DB — the recurring "Skill was never
    /// imported" bug. The rule lives HERE, next to the sources, rather than in a portal that would
    /// have to know AI's partition names to apply it.</para>
    /// </summary>
    internal static IReadOnlySet<string>? ServeFromPartitions(IConfiguration? configuration)
    {
        if (configuration is null)
            return null;   // no configuration supplied ⇒ exactly the absent-key answer: in-memory serving

        var configured = configuration
            .GetSection("Features:StaticRepoSync:Partitions")
            .GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (configured.Count == 0)
            return null;
        if (configured.Overlaps(AiContentSources.ContentPartitions))
            configured.UnionWith(AiContentSources.ContentPartitions);
        return configured;
    }

    /// <summary>
    /// Everything the portal's composition root used to register for AI, moved verbatim: the
    /// per-user CLI Connect surface, the model-provider service behind Settings → Models, and the
    /// static-repo AI content sources when this deployment serves them from the DB.
    /// </summary>
    private static IServiceCollection AddAiComposition(IServiceCollection services, IConfiguration? configuration)
    {
        // Settings → Models. The Connect token sink is the seam that keeps this the right way
        // round: the engine persists a captured CLI token as an encrypted ModelProvider without
        // the portal assembly ever entering the picture.
        services.AddSingleton<ModelProviderService>();
        services.AddSingleton<IConnectTokenSink, ConnectTokenSink>();

        // Mesh-scoped singleton holding the live login Process between "show URL" and "paste
        // code" (instance dictionary, 5-minute timeout) — never static.
        services.AddSingleton<ConnectSessionManager>();

        if (configuration is null)
            return services;

        // Reactive skill→file sync for the co-hosted CLIs: writes AGENTS.md (the base
        // "mesh-is-via-MCP" instructions plus a LISTING of the nodeType:Skill catalog) to the shared
        // volume and keeps it in sync as skill nodes change. Skill BODIES never reach disk — the
        // harness reads each on demand through the meshweaver MCP `get`.
        var skillsDirectory = configuration["Skills:Directory"];
        if (string.IsNullOrWhiteSpace(skillsDirectory))
        {
            // Defaults to a sibling of the per-user .claude root (e.g. /mnt/users → /mnt/users/_skills).
            var claudeRoot = configuration["ClaudeCode:ConfigDirRoot"]?.TrimEnd('/', '\\');
            skillsDirectory = string.IsNullOrEmpty(claudeRoot) ? null : $"{claudeRoot}/_skills";
        }
        var anyCli = configuration.GetValue("Features:Ai:Clis:ClaudeCode", true)
                     || configuration.GetValue("Features:Ai:Clis:Copilot", true);
        if (anyCli && !string.IsNullOrWhiteSpace(skillsDirectory))
        {
            services.Configure<AgentSkillSyncOptions>(o => o.Directory = skillsDirectory);
            services.AddHostedService<AgentSkillSyncService>();
        }

        // Each gated CLI registers its own IConnectStrategy. Default ON, matching the portal's
        // MemexFeatureOptions default, so delisting is a deliberate act rather than a typo.
        if (configuration.GetValue("Features:Ai:Clis:ClaudeCode", true))
        {
            services.AddSingleton<IConnectStrategy, ClaudeConnectStrategy>();
            services.Configure<ClaudeConnectOptions>(options =>
            {
                configuration.GetSection("ClaudeConnect").Bind(options);
                // `claude setup-token` renders an Ink UI that needs a real TTY, so the co-hosted
                // Linux portal defaults the PTY wrapper ON unless the deployment says otherwise.
                if (configuration["ClaudeConnect:UsePseudoTerminal"] is null && !OperatingSystem.IsWindows())
                    options.UsePseudoTerminal = true;
                // Mirror the per-user .claude root the co-hosted client uses, so each user logs in
                // under their own directory.
                if (string.IsNullOrEmpty(options.ConfigDirRoot))
                    options.ConfigDirRoot = configuration["ClaudeCode:ConfigDirRoot"];
            });
        }

        // The static-repo import half: register the built-in AI content sources whenever this
        // deployment serves any AI partition from the DB. The portal used to do this on the
        // engine's behalf, which is why it had to know the partition names.
        var serve = ServeFromPartitions(configuration);
        if (serve is not null && serve.Overlaps(AiContentSources.ContentPartitions))
        {
            services.AddBuiltInAiContentSources();

            // 🔑 The provider-credential seed: {Section}:ApiKey → the ModelProvider node, once the
            // import has landed the catalog. Only on the DB-synced path — the one where a provider
            // node can outlive the configuration that seeded it. On the in-memory path
            // BuiltInLanguageModelProvider re-projects configuration into the served node on every
            // read, so there is nothing to converge and nothing to persist.
            if (serve.Contains(ModelProviderNodeType.RootNamespace))
                services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService, ProviderCredentialSeedHostedService>();
        }

        return services;
    }
}
