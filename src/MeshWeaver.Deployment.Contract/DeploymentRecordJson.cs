using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;

namespace MeshWeaver.Deployment;

/// <summary>
/// The record on the wire and in configuration — ONE shape, used by the Aspire adapter to emit it,
/// by the portal to bind it, and by anyone handing a record file to the setup wizard or a
/// <c>Provision</c> action.
///
/// <para><b>The configuration section is a single JSON value, <c>Deployment:Record</c>
/// (environment: <c>Deployment__Record</c>).</b> Not one key per field: the record is a tree of
/// immutable records with <c>init</c> properties, immutable lists and sorted dictionaries, and a
/// nested <c>$type</c> on the mesh — <c>IConfiguration.Bind</c> handles none of those faithfully,
/// while one JSON value round-trips the exact document the mesh stores (minus the mesh's own
/// <c>$type</c>, which this reader ignores). A Kubernetes ConfigMap and an Aspire container env
/// carry the same value byte-for-byte, so the portal reads its own record identically on both.
/// The per-field keys the portal ALSO reads (<c>Storage__*</c>, <c>PluginCatalog__*</c>, …) are
/// the DERIVED surface, <see cref="DeploymentPortalConfig.PortalConfig"/>, emitted beside it.</para>
///
/// <para>The mesh's own node JSON differs in ONE respect: it carries <c>$type</c> discriminators
/// (<c>"$type": "DeploymentContent"</c>, <c>"KeyVaultSecretsSpec"</c>, …) written by the mesh's
/// polymorphic converter. <see cref="Read"/> accepts either form — unknown members are skipped —
/// which is what the round-trip test over the real <c>Deployments/memex</c> and
/// <c>Deployments/pearl</c> records asserts.</para>
/// </summary>
public static class DeploymentRecordJson
{
    /// <summary>The configuration key the portal binds its record from.</summary>
    public const string ConfigurationKey = "Deployment:Record";

    /// <summary>The same key as an environment variable name.</summary>
    public const string EnvironmentKey = "Deployment__Record";

    /// <summary>
    /// Wire options: camelCase, nulls omitted on write, unknown members skipped on read (the mesh's
    /// <c>$type</c>), case-insensitive property matching (a hand-written record file forgives case).
    /// </summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // The computed getters (PluginRepoMount.EffectiveRef, ResourceEnvelope.IsEmpty,
        // StartupProbeSpec.BudgetSeconds, …) are derivations, not fields: the mesh never writes
        // them (no fleet record carries one), and a record injected as Deployment:Record must read
        // as the record does at rest. RecordRoundTripTest pins this on three real records.
        IgnoreReadOnlyProperties = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        WriteIndented = false,
    };

    /// <summary>The record as one JSON document (no <c>$type</c>; nulls omitted).</summary>
    public static string Write(DeploymentContent record, bool indented = false) =>
        JsonSerializer.Serialize(record, indented ? new JsonSerializerOptions(Options) { WriteIndented = true } : Options);

    /// <summary>
    /// A record from JSON — the configuration value, a record file, or the mesh node's
    /// <c>content</c> object. Throws on malformed JSON; returns null for an empty value.
    /// </summary>
    public static DeploymentContent? Read(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<DeploymentContent>(json, Options);

    /// <summary>
    /// The record a process was started with: <see cref="ConfigurationKey"/> parsed, or null when
    /// the configuration carries none (a portal not launched from a record — the dev Monolith, an
    /// image started by hand). Never guesses: a present-but-malformed value throws, because a
    /// record the process cannot read is not "no record".
    /// </summary>
    public static DeploymentContent? FromConfiguration(IConfiguration configuration) =>
        Read(configuration[ConfigurationKey]);

    /// <summary>
    /// A record from a file on disk — what a developer hands to the setup wizard or a
    /// <c>Provision</c> action, and what <c>AddMemex(...).PublishRecord(path)</c> writes. Accepts
    /// both the bare record and a full mesh node (<c>{"content": {…}}</c>).
    /// </summary>
    public static DeploymentContent ReadFile(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var content = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("content", out var c) ? c : root;
        return content.Deserialize<DeploymentContent>(Options)
               ?? throw new InvalidDataException($"'{path}' holds no Deployment record");
    }

    /// <summary>Writes the record as an indented JSON document (the record file shape).</summary>
    public static void WriteFile(DeploymentContent record, string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, Write(record, indented: true) + Environment.NewLine);
    }
}
