using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

// 🚨 The NAMESPACE is part of the binary contract, and it does NOT follow the assembly.
// This type used to live in MeshWeaver.AI; a module compiled before it moved holds a TypeRef to
// `MeshWeaver.AI.ProviderKeyProtector` scoped to the `MeshWeaver.AI` AssemblyRef. The
// [assembly: TypeForwardedTo] in src/MeshWeaver.AI/TypeForwards.cs redirects that TypeRef here —
// but ONLY while the full type NAME is unchanged, because a forwarder CANNOT rename. So
// `namespace MeshWeaver.AI;` inside MeshWeaver.Mesh.Contract is DELIBERATE AND PERMANENT.
// Tidying it to `MeshWeaver.Mesh.Security` is the #2370 outage all over again (#2398);
// MovedTypeBinaryContractTest and scripts/check-type-forwards.py both refuse it.
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
    /// Encrypts a secret into the <c>enc:v1:</c> stored form. Idempotent — an already-tagged value
    /// is returned unchanged — and null/empty is returned as-is, because a caller that has no
    /// secret is not storing one (a keyless provider such as Copilot or the local Claude Code CLI).
    ///
    /// <para>🚨 With no master key configured this <b>THROWS</b>. It used to return the plaintext
    /// unchanged, and that passthrough is the defect: an unconfigured deployment persisted raw
    /// credentials into node content with nothing failing and nothing logged at the call site —
    /// found in production on 2026-08-24 with a live OpenRouter key in cleartext in
    /// <c>Provider/OpenRouter</c>. A missing master key is a CONFIGURATION FAULT, not a degraded
    /// mode.</para>
    ///
    /// <para>A caller that has a structured, non-writing refusal to report — the boot seed's
    /// <c>ProviderSeedOutcome.RefusedUnprotected</c> — must ask <see cref="IMasterKeyProvider"/>
    /// BEFORE calling this and skip the write, rather than discovering the state from a throw.
    /// Anywhere else the throw IS the answer: refusing loudly beats storing a live credential.</para>
    /// </summary>
    /// <param name="plaintext">The secret to protect; null/empty is returned as-is.</param>
    /// <returns>The encrypted stored form, or the input unchanged when it is null/empty or already tagged.</returns>
    /// <exception cref="InvalidOperationException">
    /// No master key is configured, so the value could only be stored in plaintext.
    /// </exception>
    public string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;
        // Idempotent — never double-encrypt an already-tagged value.
        if (plaintext.StartsWith("enc:", StringComparison.Ordinal)) return plaintext;

        var key = masterKeyProvider.GetMasterKey();
        if (key is null)
            // 🚨 REFUSE. Naming the setting AND the two ways to avoid storing a literal at all is
            // part of the fix: a refusal that does not say what to do next is how the passthrough
            // survived. Never echo the value — a key that has been echoed must be rotated.
            throw new InvalidOperationException(
                "Refusing to encrypt a secret for storage: no master key is configured "
                + $"({ConfigMasterKeyProvider.ConfigKey}), so the value could only be persisted in "
                + "PLAINTEXT. Set the master key for this deployment (env "
                + "'Ai__KeyProtection__MasterKey'), or reference the credential from the host's "
                + "secret store instead of storing a literal (ModelDefinition.ApiKeySecretRef, or "
                + "the provider's {section}:ApiKey configuration).");

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
