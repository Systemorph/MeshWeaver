namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Signals that an API-token validation could not be carried out AT ALL — a read
/// timeout, a storage fault, an unroutable ApiToken hub. This is a RETRYABLE
/// infrastructure condition, deliberately distinct from a definitive negative
/// verdict (unknown / mismatching / revoked / expired token): during a platform
/// hiccup a client holding a perfectly valid token must be told "retry shortly",
/// never "your token is invalid" (issue #637 — the indistinguishable 401 forced
/// clients into pointless re-authentication).
///
/// <para>Transport handshakes (SignalR / gRPC) throw this instead of silently
/// degrading the connection to Anonymous, so the boundary can abort with a
/// retryable status (<c>StatusCode.Unavailable</c> on gRPC, a speaking
/// <c>HubException</c> on SignalR) and the client's reconnect logic retries with
/// the same token.</para>
/// </summary>
public class TokenValidationUnavailableException : Exception
{
    /// <summary>Creates the exception with a message naming the infrastructure fault.</summary>
    public TokenValidationUnavailableException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception wrapping the underlying infrastructure fault.</summary>
    public TokenValidationUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
