namespace MeshWeaver.Hosting.Grpc;

/// <summary>
/// Options for the gRPC mesh transport, bound from the <c>Grpc</c> configuration section.
/// </summary>
public class GrpcOptions
{
    /// <summary>The configuration section the options bind from.</summary>
    public const string SectionName = "Grpc";

    /// <summary>
    /// Local port of the <b>trusted</b> gRPC endpoint — the loopback-bound Kestrel endpoint
    /// (<c>http://127.0.0.1:{TrustedPort}</c>) reserved for services that ship <em>in the same
    /// deployment</em> as the portal: the co-located node / bun / python gates. Only containers in
    /// the same pod share the loopback network namespace, so reachability of this port <b>is</b> the
    /// trust boundary — kernel-enforced, no shared secret, nothing to rotate.
    ///
    /// <para>A connection arriving on this port authenticates as a trusted service: no Bearer token
    /// is required, its default identity is the well-known System principal, and an
    /// <c>AccessContext</c> already carried on an injected delivery is <b>passed through</b> instead
    /// of re-stamped — so a gate executing a user's request writes back under that user's identity,
    /// exactly like the in-process C# kernel does (see AccessContextPropagation.md).</para>
    ///
    /// <para><c>null</c> (default) disables the trusted path entirely: every connection
    /// authenticates via API token and is re-stamped with the validated identity.</para>
    /// </summary>
    public int? TrustedPort { get; set; }
}
