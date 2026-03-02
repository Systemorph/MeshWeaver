using System.Text.Json.Serialization;

namespace Memex.Portal.Shared.Admin;

/// <summary>
/// Unified content model for the Admin node (nodeType "Platform").
/// Combines initialization state and authentication provider configuration.
/// </summary>
public record PlatformSettings
{
    /// <summary>Current application schema version.</summary>
    public string Version { get; init; } = "3.0";

    /// <summary>When the application was first initialized.</summary>
    public DateTimeOffset InitializedAt { get; init; }

    /// <summary>Who performed the initialization (user ObjectId).</summary>
    public string? InitializedBy { get; init; }

    /// <summary>Whether developer login is enabled.</summary>
    public bool EnableDevLogin { get; init; } = true;

    /// <summary>
    /// Azure KeyVault URI for resolving client secrets at startup.
    /// Example: "https://myvault.vault.azure.net/"
    /// When null/empty, secret resolution is skipped (dev mode).
    /// </summary>
    public string? KeyVaultUri { get; init; }

    /// <summary>
    /// Enabled provider configurations indexed by provider name.
    /// Only populated (enabled + configured) providers are persisted.
    /// </summary>
    public Dictionary<string, AuthProviderEntry> Providers { get; init; } = new();
}

/// <summary>
/// Configuration for a single OAuth provider.
/// Only the variable parts are stored — endpoints, scopes, and claim mappings
/// are hardcoded constants in OAuthProviderDefinitions.
/// </summary>
public record AuthProviderEntry
{
    /// <summary>The OAuth App/Client ID.</summary>
    public string AppId { get; init; } = "";

    /// <summary>
    /// Name of the secret in Azure KeyVault that holds the client secret.
    /// Example: "memex-microsoft-client-secret"
    /// </summary>
    [JsonPropertyName("keyVaultSecretName")]
    public string KeyVaultClientSecretName { get; init; } = "";

    /// <summary>Tenant ID (Microsoft-specific, defaults to "common").</summary>
    public string? TenantId { get; init; }
}
