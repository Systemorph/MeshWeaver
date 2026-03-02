using System.Collections.Concurrent;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Admin;

/// <summary>
/// Service for interacting with Azure Key Vault.
/// Lists secrets and resolves secret values using DefaultAzureCredential.
/// </summary>
public interface IKeyVaultService
{
    /// <summary>
    /// Lists enabled secret names from the specified vault.
    /// </summary>
    Task<List<string>> ListSecretsAsync(string vaultUri);

    /// <summary>
    /// Gets the current value of a secret from the specified vault.
    /// Returns null if the secret does not exist or cannot be read.
    /// </summary>
    Task<string?> GetSecretValueAsync(string vaultUri, string secretName);
}

public class KeyVaultService(ILogger<KeyVaultService> logger) : IKeyVaultService
{
    private readonly ConcurrentDictionary<string, SecretClient> _clients = new(StringComparer.OrdinalIgnoreCase);

    public async Task<List<string>> ListSecretsAsync(string vaultUri)
    {
        var client = GetClient(vaultUri);
        var secretNames = new List<string>();

        await foreach (var secret in client.GetPropertiesOfSecretsAsync())
        {
            if (secret.Enabled == true)
                secretNames.Add(secret.Name);
        }

        secretNames.Sort(StringComparer.OrdinalIgnoreCase);
        return secretNames;
    }

    public async Task<string?> GetSecretValueAsync(string vaultUri, string secretName)
    {
        try
        {
            var client = GetClient(vaultUri);
            var response = await client.GetSecretAsync(secretName);
            return response.Value.Value;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get secret '{SecretName}' from vault {VaultUri}.", secretName, vaultUri);
            return null;
        }
    }

    private SecretClient GetClient(string vaultUri)
        => _clients.GetOrAdd(vaultUri, uri => new SecretClient(new Uri(uri), new DefaultAzureCredential()));
}
