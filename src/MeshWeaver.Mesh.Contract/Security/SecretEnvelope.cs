using System.Collections.Immutable;
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
/// <c>deployment|object</c>), to the key id and version, and to the <b>ephemeral public key exactly as
/// transmitted</b> (its SPKI bytes), each field length-prefixed so no split of the same characters between key id
/// and context authenticates as another. A sealed value cannot be replayed onto another deployment or object:
/// unsealing with a different context fails authentication. Binding the ephemeral key closes the one tamper ECDH
/// alone cannot see: the negated point <c>(x, p − y)</c> yields the same shared x-coordinate, hence the same AES
/// key, so without it a rewritten <c>epk</c> would still authenticate.</para>
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

    /// <summary>
    /// The largest wire JSON <see cref="TryParse"/> reads. Every field is bounded too (the base64 of a P-256
    /// SPKI key, of 16/12/16 random bytes, and of at most <see cref="MaxPlaintextBytes"/> of ciphertext), so an
    /// envelope that could never have been sealed is refused BEFORE anything is decoded or allocated for it.
    /// </summary>
    public const int MaxWireChars = 16384;

    private const int MaxKeyIdChars = 64;
    private const int MaxEphemeralKeyChars = 256;
    private const int MaxCiphertextChars = (MaxPlaintextBytes + 2) / 3 * 4;

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
        // The same bound the reader applies (WithinBounds): never write an envelope that TryParse/Unseal refuse.
        if (keyId is not { Length: > 0 and <= MaxKeyIdChars })
            throw new ArgumentException($"a key id is 1..{MaxKeyIdChars} characters", nameof(keyId));
        using var recipient = ECDiffieHellman.Create();
        recipient.ImportSubjectPublicKeyInfo(recipientPublicKey, out _);
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ephemeralPublicKey = ephemeral.ExportSubjectPublicKeyInfo();
        var key = DeriveKey(ephemeral.DeriveRawSecretAgreement(recipient.PublicKey), salt);
        var ciphertext = new byte[data.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(nonce, data, ciphertext, tag, Aad(CurrentVersion, keyId, context, ephemeralPublicKey));
        CryptographicOperations.ZeroMemory(key);
        return new SecretEnvelope(CurrentVersion, keyId,
            Convert.ToBase64String(ephemeralPublicKey),
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
            if (Version != CurrentVersion || !WithinBounds()) return null;
            var ciphertext = Convert.FromBase64String(Ciphertext);
            var salt = Convert.FromBase64String(Salt);
            var nonce = Convert.FromBase64String(Nonce);
            var tag = Convert.FromBase64String(Tag);
            if (ciphertext.Length is 0 or > MaxPlaintextBytes || salt.Length != 16 || nonce.Length != 12 || tag.Length != 16)
                return null;
            using var recipient = ECDiffieHellman.Create();
            recipient.ImportPkcs8PrivateKey(recipientPrivateKey, out _);
            var ephemeralPublicKey = Convert.FromBase64String(EphemeralPublicKey);
            using var sender = ECDiffieHellman.Create();
            sender.ImportSubjectPublicKeyInfo(ephemeralPublicKey, out _);
            if (sender.KeySize != 256) return null;
            var key = DeriveKey(recipient.DeriveRawSecretAgreement(sender.PublicKey), salt);
            var plain = new byte[ciphertext.Length];
            using (var aes = new AesGcm(key, 16))
                aes.Decrypt(nonce, ciphertext, tag, plain, Aad(Version, KeyId, context, ephemeralPublicKey));
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

    /// <summary>
    /// Parses an envelope; null for anything that is not one — including input longer than
    /// <see cref="MaxWireChars"/> and any field longer than a sealed envelope can carry, refused before it is
    /// deserialized or decoded.
    /// </summary>
    public static SecretEnvelope? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxWireChars) return null;
        try
        {
            var envelope = JsonSerializer.Deserialize<SecretEnvelope>(json, Json);
            return envelope is { KeyId.Length: > 0, EphemeralPublicKey.Length: > 0, Salt.Length: > 0, Nonce.Length: > 0, Ciphertext.Length: > 0, Tag.Length: > 0 }
                   && envelope.WithinBounds()
                ? envelope
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The field lengths a sealed envelope can have, checked before any field is decoded.
    private bool WithinBounds()
        => KeyId is { Length: > 0 and <= MaxKeyIdChars }
           && EphemeralPublicKey is { Length: > 0 and <= MaxEphemeralKeyChars }
           && Salt is { Length: 24 }
           && Nonce is { Length: 16 }
           && Tag is { Length: 24 }
           && Ciphertext is { Length: > 0 and <= MaxCiphertextChars };

    private static byte[] DeriveKey(byte[] sharedSecret, byte[] salt)
    {
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, salt, Encoding.UTF8.GetBytes(KdfInfo));
        CryptographicOperations.ZeroMemory(sharedSecret);
        return key;
    }

    // 🚨 Every variable field is LENGTH-PREFIXED (4-byte big-endian byte count, then its UTF-8 bytes), so the
    // encoding is injective: ("k1|a", "b") and ("k1", "a|b") are different associated data. A separator alone
    // would let a caller move it between the key id and the context and still authenticate. The ephemeral public
    // key's SPKI bytes are the last field: ECDH authenticates only the shared x-coordinate, which the negated point
    // shares, so the transmitted key itself must be authenticated.
    private static byte[] Aad(int version, string keyId, string context, byte[] ephemeralPublicKey)
    {
        var prefix = Encoding.UTF8.GetBytes($"mw-secret-envelope v{version}");
        var kid = Encoding.UTF8.GetBytes(keyId);
        var ctx = Encoding.UTF8.GetBytes(context);
        var aad = new byte[prefix.Length + 4 + kid.Length + 4 + ctx.Length + 4 + ephemeralPublicKey.Length];
        var span = aad.AsSpan();
        prefix.CopyTo(span);
        span = span[prefix.Length..];
        span = WriteField(span, kid);
        span = WriteField(span, ctx);
        WriteField(span, ephemeralPublicKey);
        return aad;
    }

    private static Span<byte> WriteField(Span<byte> span, byte[] field)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(span, field.Length);
        field.CopyTo(span[4..]);
        return span[(4 + field.Length)..];
    }
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

    /// <summary>
    /// The outgoing keys still inside their grace window, each with its own deadline. 🚨 EVERY unexpired one is
    /// kept: two rotations inside one window (K0 → K1 → K2) must not drop K0 before its deadline, or a value
    /// sealed to it stops opening early.
    ///
    /// <para>🚨 <b>Never null — empty when there are none.</b> A rotation's update lambda runs on the WRITER's
    /// mirror and reaches the owner as a merge patch carrying the writer's base values. The owner merges an array
    /// field three-way by element identity (the writer's additions and removals onto the LIVE list), but a field
    /// whose base is null carries no base at all, and the patch value then REPLACES the live one: a rotation
    /// delayed behind two others (K0 → KA → KC) would overwrite <c>[K0, KA]</c> with <c>[K0]</c> and strand every
    /// value sealed to KA. An empty list is a base, so that write appends instead.</para>
    /// </summary>
    public ImmutableList<RetiredSealingKey> Retired { get; init; } = [];

    /// <summary>When a new key is due.</summary>
    public DateTimeOffset? RotateAfter { get; init; }

    /// <summary>
    /// The protected private key that opens <paramref name="keyId"/> at <paramref name="now"/>: the current one, or
    /// a retired one inside its window; null otherwise.
    ///
    /// <para>🚨 A key retires ONCE, and its FIRST deadline holds. Two rotations computed from the same key at
    /// different times each append a retirement for it, with different deadlines, and the owner's array merge keeps
    /// both (they differ as elements). The earliest entry for the id decides; a later one never extends the
    /// window. An expired entry is kept as a TOMBSTONE (see <see cref="RotatedTo"/>), so the first deadline is
    /// still here to decide after its key material is gone.</para>
    /// </summary>
    public string? ProtectedPrivateKeyFor(string keyId, DateTimeOffset now)
        => keyId == CurrentId ? ProtectedCurrentPrivateKey
            : (Retired ?? []).Where(r => r.Id == keyId).MinBy(r => r.ValidUntil) is { } retired && retired.StillValid(now)
                ? retired.ProtectedPrivateKey
                : null;

    /// <summary>
    /// This key retired in favour of <paramref name="next"/> at <paramref name="now"/>: the outgoing current key
    /// joins the retired keys for <paramref name="grace"/>, and every retired key already past its deadline loses
    /// its key material but stays as a tombstone. Idempotent by key id: a key already retired keeps its first
    /// (earliest) deadline, and duplicate entries a concurrent rotation merged in collapse to that one. Pure.
    ///
    /// <para>🚨 <b>Expired keys are tombstoned, never dropped.</b> This list reaches the owner as a three-way merge
    /// by element, so a writer removes only the entries its mirror held. Dropping an expired first entry would
    /// leave behind a later entry for the same key that the writer never saw, or one a delayed rotation adds after
    /// the cleanup, and that later deadline would open the expired key again. The tombstone keeps the first
    /// deadline for the id, so <see cref="ProtectedPrivateKeyFor"/> still decides on it. Tombstones are one small
    /// entry per rotation and carry no secret.</para>
    /// </summary>
    public SecretSealingKey RotatedTo(SecretSealingKey next, DateTimeOffset now, TimeSpan grace)
        => next with
        {
            Retired = (Retired ?? [])
                .Append(new RetiredSealingKey(CurrentId, ProtectedCurrentPrivateKey, now.Add(grace)))
                .GroupBy(r => r.Id)
                .Select(sameKey => sameKey.MinBy(r => r.ValidUntil)!)
                .Select(first => first.StillValid(now) ? first : first.Tombstone())
                .ToImmutableList(),
        };
}

/// <summary>An outgoing sealing key, accepted until <paramref name="ValidUntil"/>.</summary>
/// <param name="Id">The key id envelopes sealed to it name.</param>
/// <param name="ProtectedPrivateKey">Its private key (PKCS#8, base64), protected by the platform key protector.</param>
/// <param name="ValidUntil">The instant it stops opening.</param>
public sealed record RetiredSealingKey(string Id, string ProtectedPrivateKey, DateTimeOffset ValidUntil)
{
    /// <summary>Whether it is still accepted at <paramref name="now"/>.</summary>
    public bool StillValid(DateTimeOffset now) => !string.IsNullOrWhiteSpace(ProtectedPrivateKey) && now < ValidUntil;

    /// <summary>The same retirement without its key material: it opens nothing, and its deadline still decides.</summary>
    public RetiredSealingKey Tombstone() => this with { ProtectedPrivateKey = "" };
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
    /// <summary>
    /// The route, served by the control instance. ANONYMOUS and read-only: it publishes a public key and
    /// authenticates nobody, so a consumer must not treat an answer from it as an authentication boundary. What a
    /// value sealed to the key is FOR is gated elsewhere (it is accepted only inside an event signed with the
    /// deployment's own key).
    /// </summary>
    public const string Route = "/api/instances/seal-key";

    /// <summary>The algorithm named in the answer.</summary>
    public const string Algorithm = "ECDH-P256+HKDF-SHA256+A256GCM";

    /// <summary>Serializer options both sides use.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The answer: key id, base64 SPKI public key, algorithm and envelope version.</summary>
    public sealed record Response(string KeyId, string PublicKey, string Algorithm, int Version);
}
