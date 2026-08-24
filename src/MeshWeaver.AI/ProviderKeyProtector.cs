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
    /// already-tagged value is returned unchanged).
    ///
    /// <para>🚨 THROWS when no master key is configured, rather than storing the key in plaintext.
    /// Writes fail closed; <see cref="Unprotect"/> stays tolerant so a deployment already holding
    /// legacy plaintext keeps working after an upgrade. Fail on the way IN, tolerate on the way
    /// OUT.</para>
    /// </summary>
    /// <param name="plaintext">The key to protect; null/empty is returned as-is.</param>
    /// <returns>The encrypted <c>enc:v1:</c> form, or the value unchanged when it is null/empty or
    /// already tagged. There is no longer a "returns the original when encryption is disabled" case
    /// — that WAS the plaintext-persistence defect; with no master key this throws.</returns>
    /// <exception cref="InvalidOperationException">No master key is configured, so the key cannot be
    /// stored without writing it in the clear.</exception>
    public string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;
        // Idempotent — never double-encrypt an already-tagged value.
        if (plaintext.StartsWith("enc:", StringComparison.Ordinal)) return plaintext;

        var key = masterKeyProvider.GetMasterKey();
        if (key is null)
            // 🚨 FAIL CLOSED. This used to `return plaintext` — a silent passthrough that PERSISTED
            // A RAW PROVIDER KEY whenever no master key was configured. Nothing failed and nothing
            // logged at the call site, so ProviderKeyEncryptionTest stayed green (it configures a
            // master key) while an unconfigured deployment quietly stored cleartext. Found
            // 2026-08-24: a live OpenRouter key sitting in plaintext in Provider/OpenRouter node
            // content, readable by anyone with read on that namespace.
            //
            // A provider key is the one value here that must never be written in the clear, so a
            // missing master key is a CONFIGURATION FAULT, not a degraded mode. Refusing is also
            // what makes the encryption invariant true rather than merely usual.
            //
            // Unprotect stays a passthrough on purpose — see below.
            throw new InvalidOperationException(
                $"Refusing to store a provider key: no master key is configured "
                + $"({ConfigMasterKeyProvider.ConfigKey}), so the key would be persisted in "
                + "PLAINTEXT. Configure the master key for this deployment, or reference the "
                + "credential from the host's secret store instead of storing a literal "
                + "(ModelDefinition.ApiKeySecretRef, or the provider's {section}:ApiKey config).");

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
