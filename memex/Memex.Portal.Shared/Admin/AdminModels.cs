namespace Memex.Portal.Shared.Admin;

/// <summary>
/// Content for the Admin/Initialization node.
/// Tracks the application version and setup state.
/// </summary>
public record InitializationContent
{
    /// <summary>Current application schema version.</summary>
    public string Version { get; init; } = "3.0";

    /// <summary>When the application was first initialized.</summary>
    public DateTimeOffset InitializedAt { get; init; }

    /// <summary>Who performed the initialization (user ObjectId).</summary>
    public string? InitializedBy { get; init; }
}

/// <summary>
/// Content for the Admin/AuthProviders node.
/// Maps each provider to its App ID and KeyVault secret name.
/// </summary>
public record AuthProviderSettings
{
    /// <summary>Whether developer login is enabled.</summary>
    public bool EnableDevLogin { get; init; } = true;

    /// <summary>
    /// Azure KeyVault URI for resolving client secrets at startup.
    /// Example: "https://myvault.vault.azure.net/"
    /// When null/empty, secret resolution is skipped (dev mode).
    /// </summary>
    public string? KeyVaultUri { get; init; }

    /// <summary>Provider configurations indexed by provider name (Microsoft, Google, LinkedIn, Apple).</summary>
    public Dictionary<string, AuthProviderEntry> Providers { get; init; } = new();
}

/// <summary>
/// Configuration for a single OAuth provider.
/// Only the variable parts are stored — endpoints, scopes, and claim mappings
/// are hardcoded constants in OAuthProviderDefinitions.
/// </summary>
public record AuthProviderEntry
{
    /// <summary>Whether this provider is enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>The OAuth App/Client ID.</summary>
    public string AppId { get; init; } = "";

    /// <summary>
    /// Name of the secret in Azure KeyVault that holds the client secret.
    /// Example: "memex-microsoft-client-secret"
    /// </summary>
    public string KeyVaultSecretName { get; init; } = "";

    /// <summary>Tenant ID (Microsoft-specific, defaults to "common").</summary>
    public string? TenantId { get; init; }
}

/// <summary>
/// Content for the Admin/Settings node.
/// Global administrative settings.
/// </summary>
public record AdminSettings
{
    /// <summary>
    /// ObjectIds of users who have admin access to the platform.
    /// These users can access the /admin pages and modify settings.
    /// </summary>
    public List<string> AdminUsers { get; init; } = new();
}
