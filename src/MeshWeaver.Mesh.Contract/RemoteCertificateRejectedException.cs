using System.Net.Security;
using System.Security.Authentication;

namespace MeshWeaver.Mesh;

/// <summary>
/// The remote server's TLS certificate failed validation, and the platform's default validation
/// callback rejected it. Raised from the callback itself, where the verdict is made, so it reaches a
/// caller as the inner exception of the <c>HttpRequestException</c> — a typed marker rather than a
/// message to parse (MeshWeaver#5910).
/// </summary>
/// <remarks>
/// <para>It exists because a rejected certificate is DETERMINISTIC: retrying the handshake cannot
/// make an expired or untrusted certificate valid, so the HTTP resilience pipeline must not retry
/// it, and a page fetch can tell its caller "the site's certificate is invalid" without guessing.
/// A bare <see cref="AuthenticationException"/> is NOT that signal: <c>SslStream</c> raises other
/// handshake failures the same way (the remote closing the stream mid-handshake, for one), and
/// those are transient.</para>
/// <para>The verdict is the platform's default one — the certificate is accepted exactly when
/// <see cref="SslPolicyErrors"/> is <see cref="SslPolicyErrors.None"/>. Only the way a rejection is
/// reported changes.</para>
/// </remarks>
public sealed class RemoteCertificateRejectedException : AuthenticationException
{
    /// <summary>What the validation found wrong.</summary>
    public SslPolicyErrors PolicyErrors { get; }

    /// <summary>The chain status flags, comma-separated (for example <c>NotTimeValid</c>); empty
    /// when the failure was not in the chain.</summary>
    public string ChainStatus { get; }

    /// <summary>Creates the rejection for the given validation outcome.</summary>
    /// <param name="policyErrors">What the validation found wrong; never <see cref="SslPolicyErrors.None"/>.</param>
    /// <param name="chainStatus">The chain status flags, comma-separated; may be empty.</param>
    public RemoteCertificateRejectedException(SslPolicyErrors policyErrors, string chainStatus)
        : base(string.IsNullOrEmpty(chainStatus)
            ? $"The remote certificate is invalid according to the validation procedure: {policyErrors}"
            : $"The remote certificate is invalid because of errors in the certificate chain: {chainStatus}")
    {
        PolicyErrors = policyErrors;
        ChainStatus = chainStatus;
    }

    /// <summary>
    /// True when <paramref name="exception"/> or any exception it wraps is a
    /// <see cref="RemoteCertificateRejectedException"/>.
    /// </summary>
    /// <param name="exception">The outcome's exception, if any.</param>
    public static bool IsIn(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
            if (current is RemoteCertificateRejectedException)
                return true;
        return false;
    }
}
