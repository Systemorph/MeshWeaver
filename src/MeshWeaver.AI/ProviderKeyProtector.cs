using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI;

/// <summary>
/// AES-256-GCM <see cref="IProviderKeyProtector"/>. Stored form is
/// <c>enc:v1:{base64(nonce(12) | ciphertext | tag(16))}</c>. A fresh random
/// nonce per encryption means re-encrypting the same key yields different
/// ciphertext (semantic security) — so do not treat the stored blob as a
/// stable fingerprint of the key.
/// </summary>
public sealed class ProviderKeyProtector : IProviderKeyProtector
{
    private const string Prefix = "enc:v1:";
    private const int NonceLen = 12;   // AesGcm.NonceByteSizes
    private const int TagLen = 16;     // AesGcm.TagByteSizes max

    private readonly IMasterKeyProvider masterKeyProvider;
    private readonly ILogger<ProviderKeyProtector>? logger;

    /// <summary>
    /// Creates the protector over the given master-key source.
    /// </summary>
    /// <param name="masterKeyProvider">Supplies the AES-256 master key; a null key disables encryption (passthrough).</param>
    /// <param name="logger">Optional logger for decrypt failures and unknown-tag warnings.</param>
    public ProviderKeyProtector(IMasterKeyProvider masterKeyProvider, ILogger<ProviderKeyProtector>? logger = null)
    {
        this.masterKeyProvider = masterKeyProvider;
        this.logger = logger;
    }

    /// <summary>
    /// Encrypts a provider key into the <c>enc:v1:</c> stored form. Idempotent (an
    /// already-tagged value is returned unchanged) and a passthrough when no master
    /// key is configured.
    /// </summary>
    /// <param name="plaintext">The key to protect; null/empty is returned as-is.</param>
    /// <returns>The encrypted stored form, or the original value when encryption is disabled or skipped.</returns>
    public string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;
        // Idempotent — never double-encrypt an already-tagged value.
        if (plaintext.StartsWith("enc:", StringComparison.Ordinal)) return plaintext;

        var key = masterKeyProvider.GetMasterKey();
        if (key is null) return plaintext; // encryption disabled → passthrough

        var nonce = RandomNumberGenerator.GetBytes(NonceLen);
        var pt = Encoding.UTF8.GetBytes(plaintext);
        var ct = new byte[pt.Length];
        var tag = new byte[TagLen];

        using var gcm = new AesGcm(key, TagLen);
        gcm.Encrypt(nonce, pt, ct, tag);

        var blob = new byte[NonceLen + ct.Length + TagLen];
        Buffer.BlockCopy(nonce, 0, blob, 0, NonceLen);
        Buffer.BlockCopy(ct, 0, blob, NonceLen, ct.Length);
        Buffer.BlockCopy(tag, 0, blob, NonceLen + ct.Length, TagLen);
        return Prefix + Convert.ToBase64String(blob);
    }

    /// <summary>
    /// Decrypts a stored provider key. Untagged (legacy/plaintext) values are returned
    /// as-is; an unknown encryption tag, a missing master key, or a decrypt failure
    /// returns null (logged as a warning).
    /// </summary>
    /// <param name="stored">The stored value to decrypt; null/empty is returned as-is.</param>
    /// <returns>The plaintext key, or null when it cannot be decrypted.</returns>
    public string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return stored;
        // Legacy / disabled: untagged values are plaintext, return as-is.
        if (!stored.StartsWith("enc:", StringComparison.Ordinal)) return stored;
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            logger?.LogWarning("Stored provider key has an unknown encryption tag — cannot decrypt.");
            return null;
        }

        var key = masterKeyProvider.GetMasterKey();
        if (key is null)
        {
            logger?.LogWarning("Stored provider key is encrypted but no master key is configured — cannot decrypt.");
            return null;
        }

        try
        {
            var blob = Convert.FromBase64String(stored[Prefix.Length..]);
            if (blob.Length < NonceLen + TagLen) return null;
            var nonce = blob.AsSpan(0, NonceLen);
            var ctLen = blob.Length - NonceLen - TagLen;
            var ct = blob.AsSpan(NonceLen, ctLen);
            var tag = blob.AsSpan(NonceLen + ctLen, TagLen);
            var pt = new byte[ctLen];

            using var gcm = new AesGcm(key, TagLen);
            gcm.Decrypt(nonce, ct, tag, pt);
            return Encoding.UTF8.GetString(pt);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to decrypt a stored provider key (wrong master key or corrupt value).");
            return null;
        }
    }
}
