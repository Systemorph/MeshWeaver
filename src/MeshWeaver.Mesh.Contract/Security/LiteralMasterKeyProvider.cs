using System.Security.Cryptography;
using System.Text;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// An <see cref="MeshWeaver.AI.IMasterKeyProvider"/> over a key the caller already holds — for the
/// two places that must protect or reveal a secret with no service provider to resolve one from:
/// the configuration source that projects the instance manifest (it runs while configuration is
/// still being BUILT, so there is no DI container yet), and the setup surface that encrypts the
/// operator's answers.
///
/// <para>🚨 <b>The derivation is copied from <c>ConfigMasterKeyProvider</c> deliberately and must
/// stay identical</b> — accept any input, base64 when it parses, else UTF-8 bytes, then SHA-256 to
/// a stable 32 bytes. A different derivation over the same configured value yields a different AES
/// key, so a secret written through one and read through the other decrypts to garbage: the
/// protector answers the ciphertext unchanged, the projection drops it as unusable, and the
/// provider silently never registers. <c>InstanceMasterKeyDerivationTest</c> pins the two against
/// each other.</para>
/// </summary>
public sealed class LiteralMasterKeyProvider : MeshWeaver.AI.IMasterKeyProvider
{
    private readonly byte[]? masterKey;

    /// <summary>
    /// Derives the AES-256 key from a configured value. Null or blank leaves the key null, which is
    /// the "this install cannot store a credential" state every caller already handles.
    /// </summary>
    /// <param name="configured">The base64 key or passphrase, as <c>Ai:KeyProtection:MasterKey</c>
    /// carries it.</param>
    public LiteralMasterKeyProvider(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return;
        var trimmed = configured.Trim();
        byte[] raw;
        try { raw = Convert.FromBase64String(trimmed); }
        catch (FormatException) { raw = Encoding.UTF8.GetBytes(trimmed); }
        masterKey = SHA256.HashData(raw);
    }

    /// <inheritdoc />
    public byte[]? GetMasterKey() => masterKey;
}
