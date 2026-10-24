using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// A secret sealed to ONE recipient's public key so that it can travel through nodes, events and logs as
/// ciphertext only: <b>ECDH P-256 (ephemeral sender key) + HKDF-SHA256 + AES-256-GCM</b>, all .NET built-ins,
/// nothing custom. The envelope is versioned and names the recipient key by id, so keys rotate without
/// breaking a sealed value in flight.
///
/// <para>🚨 The associated data binds the ciphertext to its <b>context</b> (for a vault write:
/// <c>deployment|object</c>) and to the key id and version. A sealed value cannot be replayed onto another
/// deployment or object: unsealing with a different context fails authentication.</para>
///
/// <para>Wire shape (JSON, camelCase): <c>{ "v":1, "kid":"…", "epk":"base64 SPKI of the ephemeral public
/// key", "salt":"base64(16)", "nonce":"base64(12)", "ct":"base64", "tag":"base64(16)" }</c>.</para>
/// </summary>
public sealed record SecretEnvelope(
    [property: JsonPropertyName("v")] int Version,
    [property: JsonPropertyName("kid")] string KeyId,
    [property: JsonPropertyName("epk")] string EphemeralPublicKey,
    [property: JsonPropertyName("salt")] string Salt,
    [property: JsonPropertyName("nonce")] string Nonce,
    [property: JsonPropertyName("ct")] string Ciphertext,
    [property: JsonPropertyName("tag")] string Tag)
{
    /// <summary>The envelope version this build writes and reads.</summary>
    public const int CurrentVersion = 1;

    /// <summary>The HKDF <c>info</c> string: domain separation from any other use of the shared secret.</summary>
    private const string KdfInfo = "mw-secret-envelope v1";

    /// <summary>The largest plaintext sealed: a secret is a key or a password, never a document.</summary>
    public const int MaxPlaintextBytes = 8192;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Never prints the ciphertext.</summary>
    public override string ToString() => $"SecretEnvelope(v{Version}, kid={KeyId})";

    /// <summary>A new P-256 key pair for a recipient: the SPKI public key and the PKCS#8 private key, both raw bytes.</summary>
    public static (byte[] PublicKey, byte[] PrivateKey) GenerateKeyPair()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportSubjectPublicKeyInfo(), key.ExportPkcs8PrivateKey());
    }

    /// <summary>
    /// Seals <paramref name="plaintext"/> to <paramref name="recipientPublicKey"/> (SPKI) under key id
    /// <paramref name="keyId"/>, bound to <paramref name="context"/>. A fresh ephemeral key per call.
    /// </summary>
    public static SecretEnvelope Seal(string plaintext, byte[] recipientPublicKey, string keyId, string context)
    {
        var data = Encoding.UTF8.GetBytes(plaintext);
        if (data.Length is 0 or > MaxPlaintextBytes)
            throw new ArgumentException($"a sealed secret is 1..{MaxPlaintextBytes} bytes", nameof(plaintext));
        using var recipient = ECDiffieHellman.Create();
        recipient.ImportSubjectPublicKeyInfo(recipientPublicKey, out _);
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = DeriveKey(ephemeral.DeriveRawSecretAgreement(recipient.PublicKey), salt);
        var ciphertext = new byte[data.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(nonce, data, ciphertext, tag, Aad(CurrentVersion, keyId, context));
        CryptographicOperations.ZeroMemory(key);
        return new SecretEnvelope(CurrentVersion, keyId,
            Convert.ToBase64String(ephemeral.ExportSubjectPublicKeyInfo()),
            Convert.ToBase64String(salt), Convert.ToBase64String(nonce),
            Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag));
    }

    /// <summary>
    /// Opens the envelope with the recipient's private key (PKCS#8), for the same <paramref name="context"/> it
    /// was sealed for. Returns null for ANY failure (wrong key, wrong context, tampering, unknown version),
    /// never an exception that could say which: a caller must not be able to probe.
    /// </summary>
    public string? Unseal(byte[] recipientPrivateKey, string context)
    {
        try
        {
            if (Version != CurrentVersion) return null;
            using var recipient = ECDiffieHellman.Create();
            recipient.ImportPkcs8PrivateKey(recipientPrivateKey, out _);
            using var sender = ECDiffieHellman.Create();
            sender.ImportSubjectPublicKeyInfo(Convert.FromBase64String(EphemeralPublicKey), out _);
            if (sender.KeySize != 256) return null;
            var key = DeriveKey(recipient.DeriveRawSecretAgreement(sender.PublicKey), Convert.FromBase64String(Salt));
            var ciphertext = Convert.FromBase64String(Ciphertext);
            var plain = new byte[ciphertext.Length];
            using (var aes = new AesGcm(key, 16))
                aes.Decrypt(Convert.FromBase64String(Nonce), ciphertext, Convert.FromBase64String(Tag), plain, Aad(Version, KeyId, context));
            CryptographicOperations.ZeroMemory(key);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The envelope as its wire JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Parses an envelope; null for anything that is not one.</summary>
    public static SecretEnvelope? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var envelope = JsonSerializer.Deserialize<SecretEnvelope>(json, Json);
            return envelope is { KeyId.Length: > 0, EphemeralPublicKey.Length: > 0, Salt.Length: > 0, Nonce.Length: > 0, Ciphertext.Length: > 0, Tag.Length: > 0 }
                ? envelope
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static byte[] DeriveKey(byte[] sharedSecret, byte[] salt)
    {
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, salt, Encoding.UTF8.GetBytes(KdfInfo));
        CryptographicOperations.ZeroMemory(sharedSecret);
        return key;
    }

    private static byte[] Aad(int version, string keyId, string context)
        => Encoding.UTF8.GetBytes($"mw-secret-envelope|v{version}|{keyId}|{context}");
}

/// <summary>
/// A recipient's sealing key, as stored: the PUBLIC half in the clear (it is published) and the PRIVATE half only
/// as <c>enc:</c> ciphertext from the platform key protector — never as node content in the clear. The outgoing
/// key stays accepted for a bounded window so a value sealed just before a rotation can still be opened.
/// </summary>
public record SecretSealingKey
{
    /// <summary>The current key's id.</summary>
    public string CurrentId { get; init; } = "";

    /// <summary>The current public key, base64 SPKI.</summary>
    public string CurrentPublicKey { get; init; } = "";

    /// <summary>The current private key (PKCS#8, base64), protected by the platform key protector.</summary>
    public string ProtectedCurrentPrivateKey { get; init; } = "";

    /// <summary>When the current key was minted.</summary>
    public DateTimeOffset CurrentIssuedAt { get; init; }

    /// <summary>The previous key's id, during a rotation window.</summary>
    public string? PreviousId { get; init; }

    /// <summary>The previous private key, protected.</summary>
    public string? ProtectedPreviousPrivateKey { get; init; }

    /// <summary>The previous key is accepted until this instant.</summary>
    public DateTimeOffset? PreviousValidUntil { get; init; }

    /// <summary>When a new key is due.</summary>
    public DateTimeOffset? RotateAfter { get; init; }

    /// <summary>Whether the previous key is still accepted at <paramref name="now"/>.</summary>
    public bool PreviousStillValid(DateTimeOffset now)
        => !string.IsNullOrWhiteSpace(ProtectedPreviousPrivateKey) && PreviousValidUntil is { } until && now < until;
}

/// <summary>Where the sealing key lives and how it rotates.</summary>
public static class SecretSealingKeys
{
    /// <summary>The node type.</summary>
    public const string NodeType = "SecretSealingKey";

    /// <summary>The Admin namespace holding it.</summary>
    public const string Namespace = "Admin/SecretSealingKey";

    /// <summary>The one node's id.</summary>
    public const string Id = "current";

    /// <summary>The node's path.</summary>
    public const string Path = $"{Namespace}/{Id}";

    /// <summary>How long a key is current.</summary>
    public static readonly TimeSpan RotationInterval = TimeSpan.FromDays(90);

    /// <summary>How long an outgoing key is still accepted after a rotation.</summary>
    public static readonly TimeSpan RotationGrace = TimeSpan.FromDays(7);
}

/// <summary>The wire contract of <c>GET /api/instances/seal-key</c>: the public key an installation seals a secret to.</summary>
public static class SealKeyPayloads
{
    /// <summary>The route, served by the control instance and authenticated by the installation's instance key.</summary>
    public const string Route = "/api/instances/seal-key";

    /// <summary>The algorithm named in the answer.</summary>
    public const string Algorithm = "ECDH-P256+HKDF-SHA256+A256GCM";

    /// <summary>Serializer options both sides use.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The answer: key id, base64 SPKI public key, algorithm and envelope version.</summary>
    public sealed record Response(string KeyId, string PublicKey, string Algorithm, int Version);
}
