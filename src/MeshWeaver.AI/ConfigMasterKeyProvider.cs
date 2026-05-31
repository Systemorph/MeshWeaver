using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// Default <see cref="IMasterKeyProvider"/> — reads a base64 master key from
/// configuration key <see cref="ConfigKey"/> (<c>Ai:KeyProtection:MasterKey</c>).
/// For local/dev the AppHost injects it as an env var
/// (<c>Ai__KeyProtection__MasterKey</c>); in prod it should come from a deploy
/// secret or Key Vault and must NEVER be committed to a <c>src/</c> appsettings.
///
/// <para>Any input length is accepted: the configured value is hashed with
/// SHA-256 to derive the 32-byte AES key, so a passphrase or a base64 of 32
/// random bytes both work. When the key is absent/blank, returns <c>null</c> so
/// <see cref="ProviderKeyProtector"/> falls back to plaintext passthrough.</para>
///
/// <para>⚠ Rotating the configured value makes previously-stored ciphertext
/// undecryptable (different derived key) — re-save / rotate affected provider
/// keys after a master-key change. A KMS-backed <see cref="IMasterKeyProvider"/>
/// with versioned keys is the upgrade path for seamless rotation.</para>
/// </summary>
public sealed class ConfigMasterKeyProvider : IMasterKeyProvider
{
    public const string ConfigKey = "Ai:KeyProtection:MasterKey";

    private readonly byte[]? masterKey;

    public ConfigMasterKeyProvider(IServiceProvider services)
    {
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger<ConfigMasterKeyProvider>();
        var configured = services.GetService<IConfiguration>()?[ConfigKey];
        if (string.IsNullOrWhiteSpace(configured))
        {
            logger?.LogInformation(
                "No {ConfigKey} configured — provider-key encryption is DISABLED (keys stored as plaintext). " +
                "Set a base64 master key via env/secret to enable encryption at rest.", ConfigKey);
            return;
        }

        // Accept any input (passphrase or base64); derive a stable 32-byte key.
        var trimmed = configured.Trim();
        byte[] raw;
        try { raw = Convert.FromBase64String(trimmed); }
        catch (FormatException) { raw = System.Text.Encoding.UTF8.GetBytes(trimmed); }
        masterKey = SHA256.HashData(raw);
        logger?.LogInformation("Provider-key encryption ENABLED (master key from {ConfigKey}).", ConfigKey);
    }

    public byte[]? GetMasterKey() => masterKey;
}
