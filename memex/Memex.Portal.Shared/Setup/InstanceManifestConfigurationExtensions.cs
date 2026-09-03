using MeshWeaver.AI;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;

namespace Memex.Portal.Shared.Setup;

/// <summary>
/// Layers the instance manifest into a host's configuration — the step that turns the setup
/// wizard's answers into the keys the host already reads, so that NOTHING downstream has to know a
/// wizard exists.
///
/// <para>🚨 <b>Inserted at index 0, which is the whole design.</b> Configuration sources are
/// last-wins, so the manifest sitting FIRST means every appsettings value, ConfigMap key,
/// environment variable and command-line argument outranks it. That is the rule
/// <see cref="InstanceManifest"/> states for storage — <i>"it never overrides a host that stated
/// its own storage"</i> — obtained structurally rather than by a hand-written precedence check that
/// each new section would have to repeat.</para>
///
/// <para><b>A snapshot, not a live file.</b> The manifest is read once, here. It is written by the
/// setup surface, which then restarts the process; a source that reloaded would let a half-written
/// manifest reconfigure a running host mid-flight, and there is no code path that wants that.</para>
/// </summary>
public static class InstanceManifestConfigurationExtensions
{
    /// <summary>
    /// Adds the completed instance manifest under <paramref name="builder"/>'s existing sources.
    /// A host with no manifest, or an incomplete one, is left byte-identical — which is every
    /// deployment that exists today.
    /// </summary>
    /// <param name="builder">The configuration builder. For a <c>ConfigurationManager</c> this both
    /// inserts the source and rebuilds, so values are readable immediately after the call.</param>
    /// <param name="rootDirectory">The writable root the manifest and key file live on
    /// (<c>ModuleRoot.Resolve</c>).</param>
    /// <param name="configuredMasterKey">The master key the host's OWN sources answer with, so a
    /// deployment-supplied key is used and the on-disk key file is consulted only when there is
    /// none. Pass <c>configuration["Ai:KeyProtection:MasterKey"]</c>.</param>
    /// <param name="onUnreadable">Called when a manifest exists but cannot be read. Pre-DI, so
    /// production passes stderr.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IConfigurationBuilder AddInstanceManifest(
        this IConfigurationBuilder builder,
        string rootDirectory,
        string? configuredMasterKey = null,
        Action<string>? onUnreadable = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        var manifest = InstanceManifest.Read(rootDirectory, onUnreadable);
        var masterKey = InstanceMasterKey.Resolve(rootDirectory, configuredMasterKey);

        var entries = new Dictionary<string, string?>(
            InstanceManifestProjection.ToConfiguration(
                manifest,
                masterKey is null ? null : new ProviderKeyProtector(new LiteralMasterKeyProvider(masterKey))),
            StringComparer.OrdinalIgnoreCase);

        // The key file's own value is projected too, so that a wizard-provisioned install reads
        // back the key it minted. Configured-wins is preserved by the index-0 insert: when the
        // deployment supplies the key, its own source outranks this entry and this one is the same
        // value anyway (Resolve returned it).
        if (masterKey is not null)
            entries[ConfigMasterKeyProvider.ConfigKey] = masterKey;

        if (entries.Count == 0)
            return builder;

        builder.Sources.Insert(0, new MemoryConfigurationSource { InitialData = entries });
        return builder;
    }
}
