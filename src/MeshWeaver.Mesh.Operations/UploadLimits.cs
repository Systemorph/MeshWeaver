namespace MeshWeaver.AI;

/// <summary>
/// The ONE upload ceiling every transport of <see cref="MeshOperations.Upload"/> derives its
/// request-body limit from — REST multipart (<c>POST /api/mesh/upload</c>) and the MCP
/// <c>upload</c> tool's base64-in-JSON (<c>POST /mcp</c>) alike (#5135).
///
/// <para><b>Why a shared constant.</b> Kestrel refuses any request body over its own default
/// (30,000,000 bytes) unless the endpoint declares a limit. The REST route raised only the
/// multipart FORM limit to 200 MB, so the whole request was still cut at Kestrel's 30 MB; the MCP
/// route declared nothing, so a <c>tools/call</c> carrying a ~22 MB file (base64 inflates by 4/3)
/// was refused as a transport 413 that the client shows as a protocol error, never as a tool
/// error. Each route now declares a body limit computed from <see cref="MaxUploadBytes"/> for its
/// own encoding, and <see cref="MeshOperations.Upload"/> refuses a larger payload by name.</para>
///
/// <para>Both transports buffer the whole file in memory before the save, and the MCP route holds
/// the base64 text as well — which is why the ceiling is bounded, and why a file above it belongs
/// on a streaming ingest path, not on a bigger number here.</para>
/// </summary>
public static class UploadLimits
{
    /// <summary>The largest file, in DECODED bytes, any upload transport accepts: 200 MiB.</summary>
    public const long MaxUploadBytes = 200L * 1024 * 1024;

    /// <summary>
    /// Room for everything in a request that is not the file: multipart boundaries and the
    /// <c>path</c> field, or the JSON-RPC envelope and the tool arguments around the base64 text.
    /// </summary>
    public const long RequestEnvelopeBytes = 1L * 1024 * 1024;

    /// <summary>The request-body limit for a multipart upload carrying a <see cref="MaxUploadBytes"/> file.</summary>
    public const long MultipartRequestBodyLimit = MaxUploadBytes + RequestEnvelopeBytes;

    /// <summary>
    /// The request-body limit for a JSON request carrying a <see cref="MaxUploadBytes"/> file as
    /// base64: four characters per started three-byte group, plus the envelope.
    /// </summary>
    public const long Base64JsonRequestBodyLimit = 4 * ((MaxUploadBytes + 2) / 3) + RequestEnvelopeBytes;

    /// <summary>
    /// The refusal <see cref="MeshOperations.Upload"/> answers for a payload over
    /// <see cref="MaxUploadBytes"/>, in the <c>"Error: …"</c> sentinel shape every tool uses.
    /// </summary>
    /// <param name="bytes">The decoded size of the refused payload.</param>
    /// <returns>The refusal sentence.</returns>
    public static string TooLarge(long bytes)
        => $"Error: the file is {bytes:N0} bytes; the upload ceiling is {MaxUploadBytes:N0} bytes "
           + $"({MaxUploadBytes / (1024 * 1024)} MiB). Larger files need a streaming ingest path, not this tool.";
}
