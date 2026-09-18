using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Memex.Portal.Shared.Authentication;

/// <summary>Why the control instance refused a setup hand-off.</summary>
public enum HandOffRefusal
{
    /// <summary>Not a refusal.</summary>
    None,

    /// <summary>No shared secret is configured here, so the endpoint does not exist.</summary>
    NotConfigured,

    /// <summary>The request carried no signature.</summary>
    Unsigned,

    /// <summary>The signature does not match the body.</summary>
    BadSignature,

    /// <summary>The timestamp is outside the accepted window — too old, or from the future.</summary>
    Stale,

    /// <summary>This exact request has been seen before.</summary>
    Replayed,

    /// <summary>No deployment record names this instance, so there is no vault to write to.</summary>
    UnknownInstance,
}

/// <summary>
/// The door in front of the setup hand-off — the one endpoint that carries a client's secrets, from
/// a freshly provisioned instance to the vault its record names.
///
/// <para>🚨 <b>A signature alone is not enough, and this is the reason.</b> The body of this request
/// is a set of secrets. Anyone who can capture one signed request can send it again — the signature
/// stays valid forever, because it signs the body and nothing about WHEN. So the door needs three
/// checks, not one: the signature proves the sender holds the shared secret, the TIMESTAMP bounds
/// how long a captured request stays useful, and the NONCE refuses the same request twice inside
/// that window.</para>
///
/// <para><b>The nonce cache is per-process, and that is stated rather than hidden.</b> A control
/// instance running two replicas can have a replay land on the other one inside the window. The
/// honest mitigations are the short window and the fact that a hand-off is accepted only while the
/// instance still has no administrator — a replay after setup completes changes nothing. A durable
/// nonce store would close it fully and can be added without touching this decision: a nonce is not
/// a secret, so unlike the body it may be persisted.</para>
/// </summary>
public static class SetupHandOffVerifier
{
    /// <summary>How far from now a timestamp may be. Covers ordinary clock skew and no more.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    /// <summary>The header carrying the signature — the pairing the control inbox already verifies.</summary>
    public const string SignatureHeader = "X-Hub-Signature-256";

    /// <summary>The header carrying the instant the request was signed, as Unix seconds.</summary>
    public const string TimestampHeader = "X-Setup-Timestamp";

    /// <summary>The header carrying the request's unique id.</summary>
    public const string NonceHeader = "X-Setup-Nonce";

    private static readonly ConcurrentDictionary<string, DateTimeOffset> Seen = new();

    /// <summary>
    /// Whether this request may be acted on.
    /// </summary>
    /// <param name="configuredSecret">The shared webhook secret. Blank ⇒ the endpoint is off.</param>
    /// <param name="body">The exact bytes received.</param>
    /// <param name="signature">The <c>sha256=…</c> header value.</param>
    /// <param name="timestamp">The timestamp header value, Unix seconds.</param>
    /// <param name="nonce">The nonce header value.</param>
    /// <param name="now">The instant to judge against.</param>
    public static HandOffRefusal Verify(
        string? configuredSecret,
        byte[] body,
        string? signature,
        string? timestamp,
        string? nonce,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (string.IsNullOrWhiteSpace(configuredSecret))
            return HandOffRefusal.NotConfigured;
        if (string.IsNullOrWhiteSpace(signature) || string.IsNullOrWhiteSpace(nonce))
            return HandOffRefusal.Unsigned;

        if (!long.TryParse(timestamp, out var unixSeconds))
            return HandOffRefusal.Stale;
        var signedAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        // Both directions: a future timestamp would otherwise extend a captured request's life.
        if (now - signedAt > Window || signedAt - now > Window)
            return HandOffRefusal.Stale;

        var expected = Sign(body, configuredSecret!);
        if (!BootstrapSecretGate.FixedTimeEquals(expected, signature!.Trim()))
            return HandOffRefusal.BadSignature;

        // Last, so a replay check is never spent on a request that was not authentic in the first
        // place — otherwise an attacker could burn nonces they do not own.
        Forget(now);
        return Seen.TryAdd(nonce!.Trim(), now) ? HandOffRefusal.None : HandOffRefusal.Replayed;
    }

    /// <summary>The signature for a body, in the form the sender produces.</summary>
    /// <param name="body">The exact bytes.</param>
    /// <param name="secret">The shared secret.</param>
    public static string Sign(byte[] body, string secret) =>
        "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body));

    /// <summary>Drops nonces older than the window — they can no longer be replayed anyway.</summary>
    /// <param name="now">The current instant.</param>
    public static void Forget(DateTimeOffset now)
    {
        foreach (var (nonce, seenAt) in Seen)
            if (now - seenAt > Window)
                Seen.TryRemove(nonce, out _);
    }

    /// <summary>Clears the nonce cache. For tests, which must not inherit each other's state.</summary>
    public static void Reset() => Seen.Clear();
}
