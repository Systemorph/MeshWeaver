using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Admin;

/// <summary>
/// REST API for admin operations.
/// All endpoints require authentication.
/// </summary>
[ApiController]
[Route("api/admin")]
[Authorize]
public class AdminController(
    IConfiguration configuration,
    IKeyVaultService keyVaultService,
    ILogger<AdminController> logger) : ControllerBase
{
    /// <summary>
    /// Lists secret names from the configured Azure KeyVault.
    /// Used by the setup wizard to let admins pick secret names for auth providers.
    /// </summary>
    [HttpGet("keyvault/secrets")]
    public async Task<IActionResult> ListKeyVaultSecrets([FromQuery] string? vaultUri = null)
    {
        var effectiveUri = vaultUri ?? configuration["KeyVault:Uri"];
        if (string.IsNullOrWhiteSpace(effectiveUri))
        {
            return Ok(new KeyVaultSecretsResponse
            {
                Available = false,
                Message = "No KeyVault URI configured. Set KeyVault:Uri or pass ?vaultUri= parameter."
            });
        }

        try
        {
            var secretNames = await keyVaultService.ListSecretsAsync(effectiveUri);

            return Ok(new KeyVaultSecretsResponse
            {
                Available = true,
                VaultUri = effectiveUri,
                Secrets = secretNames
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to list secrets from KeyVault {VaultUri}.", effectiveUri);
            return Ok(new KeyVaultSecretsResponse
            {
                Available = false,
                Message = $"Failed to connect to KeyVault: {ex.Message}"
            });
        }
    }
}

public record KeyVaultSecretsResponse
{
    public bool Available { get; init; }
    public string? VaultUri { get; init; }
    public string? Message { get; init; }
    public List<string> Secrets { get; init; } = [];
}
