using System.Text.Json.Serialization;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Everything a user interface may know about ONE secret, and nothing more: whether it is set, its
/// state, its fingerprint, and who set it and when. It never carries the value. This is the only
/// shape a write-only secret surface reads back (<c>WriteOnlySecretSection</c>,
/// <c>SecretInventorySection</c>), whichever store holds the value: a vault written by the
/// operator, or the instance's own encrypted store (<c>InstanceSecrets</c>).
///
/// <para>🚨 The JSON names are the operator's <c>kv_status</c> field names, so a status the operator
/// reports from a vault deserializes into this record unchanged. Rename nothing here without
/// renaming it there.</para>
/// </summary>
public sealed record SecretStatus
{
    /// <summary>The secret's name: a vault object name or a configuration key.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>Whether a value is set.</summary>
    [JsonPropertyName("present")]
    public bool Present { get; init; }

    /// <summary>
    /// Whether a present value is in use: <c>true</c> or <c>false</c> for a live secret (a disabled
    /// secret keeps its value but is not used), and null when there is no live value (absent or
    /// deleted) — exactly as the operator's <c>kv_status</c> reports it.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }

    /// <summary>When the secret was first created.</summary>
    [JsonPropertyName("created")]
    public DateTimeOffset? Created { get; init; }

    /// <summary>When the current value (or state) was last changed.</summary>
    [JsonPropertyName("updated")]
    public DateTimeOffset? Updated { get; init; }

    /// <summary>When the value expires, if it has an expiry.</summary>
    [JsonPropertyName("expires")]
    public DateTimeOffset? Expires { get; init; }

    /// <summary>Whether the secret is deleted. A deleted secret can be recovered until <see cref="RecoverableUntil"/>.</summary>
    [JsonPropertyName("deleted")]
    public bool Deleted { get; init; }

    /// <summary>Until when a deleted secret can still be recovered.</summary>
    [JsonPropertyName("recoverableUntil")]
    public DateTimeOffset? RecoverableUntil { get; init; }

    /// <summary>The value's <see cref="SecretFingerprint"/>, or <see cref="SecretFingerprint.Withheld"/>, or null when unknown.</summary>
    [JsonPropertyName("fingerprint")]
    public string? Fingerprint { get; init; }

    /// <summary>Who set the current value (a user id; never a value).</summary>
    [JsonPropertyName("setBy")]
    public string? SetBy { get; init; }

    /// <summary>When the current value was set.</summary>
    [JsonPropertyName("setAt")]
    public DateTimeOffset? SetAt { get; init; }

    /// <summary>How the current value was produced: <see cref="SecretSources.Paste"/> or <see cref="SecretSources.Generate"/>.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; init; }
}

/// <summary>
/// How a secret's value was produced (<see cref="SecretStatus.Source"/>). An open vocabulary of
/// string constants (policy <c>open-vocabulary-string-constants</c>): an unknown value is shown as
/// it is, never mapped to one of these.
/// </summary>
public static class SecretSources
{
    /// <summary>A person pasted the value.</summary>
    public const string Paste = "paste";

    /// <summary>The platform generated the value and showed it once.</summary>
    public const string Generate = "generate";
}
