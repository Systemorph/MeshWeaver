using System.Text.Json;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MeshWeaver.Blazor.Portal.Authentication;

namespace Memex.Portal.Shared.Admin;

/// <summary>
/// Reads Admin/AuthProviders directly from graph storage at startup,
/// before the full MeshWeaver pipeline is up. Resolves KeyVault secrets
/// to build ExternalProviderConfig list for auth registration.
/// </summary>
public static class AdminStartupReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Reads Admin/AuthProviders from graph storage at startup.
    /// Supports FileSystem (direct file read) and PostgreSql (ADO.NET query).
    /// Returns null if the node doesn't exist (fresh install).
    /// </summary>
    public static AuthProviderSettings? ReadAuthProviders(IConfiguration config, ILogger? logger = null)
    {
        var storageType = config["Graph:Storage:Type"] ?? "FileSystem";

        try
        {
            return storageType.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase)
                ? ReadFromPostgreSql(config, logger)
                : ReadFromFileSystem(config, logger);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to read Admin/AuthProviders from {StorageType} storage. Falling back to config.", storageType);
            return null;
        }
    }

    /// <summary>
    /// Resolves KeyVault secrets and builds ExternalProviderConfig list
    /// for use by the auth registration pipeline.
    /// </summary>
    public static List<ExternalProviderConfig> ResolveProviders(
        AuthProviderSettings settings, string? keyVaultUri, ILogger? logger = null)
    {
        var result = new List<ExternalProviderConfig>();

        // Determine effective vault URI: prefer settings value, fall back to config param
        var effectiveVaultUri = !string.IsNullOrWhiteSpace(settings.KeyVaultUri)
            ? settings.KeyVaultUri
            : keyVaultUri;

        SecretClient? secretClient = null;
        if (!string.IsNullOrWhiteSpace(effectiveVaultUri))
        {
            try
            {
                secretClient = new SecretClient(
                    new Uri(effectiveVaultUri),
                    new DefaultAzureCredential());
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Failed to create KeyVault SecretClient for {VaultUri}. Secrets will not be resolved.", effectiveVaultUri);
            }
        }

        foreach (var (name, entry) in settings.Providers)
        {
            if (!entry.Enabled || string.IsNullOrWhiteSpace(entry.AppId))
                continue;

            var clientSecret = "";
            if (secretClient != null && !string.IsNullOrWhiteSpace(entry.KeyVaultSecretName))
            {
                try
                {
                    var response = secretClient.GetSecret(entry.KeyVaultSecretName);
                    clientSecret = response.Value.Value;
                    logger?.LogInformation("Resolved KeyVault secret '{SecretName}' for provider {Provider}.", entry.KeyVaultSecretName, name);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Failed to resolve KeyVault secret '{SecretName}' for provider {Provider}. Provider will be registered without a client secret.", entry.KeyVaultSecretName, name);
                }
            }

            result.Add(new ExternalProviderConfig
            {
                Name = name,
                DisplayName = name,
                ClientId = entry.AppId,
                ClientSecret = clientSecret,
                TenantId = entry.TenantId
            });
        }

        return result;
    }

    private static AuthProviderSettings? ReadFromFileSystem(IConfiguration config, ILogger? logger)
    {
        var basePath = config["Graph:Storage:BasePath"];
        if (string.IsNullOrWhiteSpace(basePath))
            return null;

        // Resolve relative path
        if (!Path.IsPathRooted(basePath))
            basePath = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), basePath));

        var filePath = Path.Combine(basePath, "Admin", "AuthProviders.json");
        if (!File.Exists(filePath))
        {
            logger?.LogDebug("Admin/AuthProviders.json not found at {Path}. Fresh install assumed.", filePath);
            return null;
        }

        var json = File.ReadAllText(filePath);
        var doc = JsonSerializer.Deserialize<JsonElement>(json, JsonOptions);

        // The file is a MeshNode JSON — content is nested under "content"
        if (doc.TryGetProperty("content", out var contentElement))
        {
            return JsonSerializer.Deserialize<AuthProviderSettings>(
                contentElement.GetRawText(), JsonOptions);
        }

        return null;
    }

    private static AuthProviderSettings? ReadFromPostgreSql(IConfiguration config, ILogger? logger)
    {
        var connectionString = config.GetConnectionString("meshweaver");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            logger?.LogDebug("No 'meshweaver' connection string found. Cannot read Admin nodes from PostgreSql.");
            return null;
        }

        // Use Npgsql directly — lightweight ADO.NET query at startup
        using var conn = new Npgsql.NpgsqlConnection(connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT content FROM mesh_nodes WHERE namespace = $1 AND id = $2 LIMIT 1";
        cmd.Parameters.AddWithValue("Admin");
        cmd.Parameters.AddWithValue("AuthProviders");

        var result = cmd.ExecuteScalar();
        if (result == null || result == DBNull.Value)
        {
            logger?.LogDebug("Admin/AuthProviders node not found in PostgreSql. Fresh install assumed.");
            return null;
        }

        var contentJson = result.ToString();
        if (string.IsNullOrWhiteSpace(contentJson))
            return null;

        return JsonSerializer.Deserialize<AuthProviderSettings>(contentJson, JsonOptions);
    }
}
