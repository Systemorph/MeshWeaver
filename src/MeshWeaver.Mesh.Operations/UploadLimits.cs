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
/// error. The REST route declares its body limit from <see cref="MaxUploadBytes"/>
/// (<see cref="MultipartRequestBodyLimit"/>); <see cref="Base64JsonRequestBodyLimit"/> is the same
/// ceiling for the MCP endpoint, which MeshWeaver.Plugins maps and declares in its companion change;
/// and <see cref="MeshOperations.Upload"/> refuses a larger payload by name on every transport.</para>
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
    /// <remarks>
    /// A tool/API sentinel like every other <c>"Error: …"</c> result of <see cref="MeshOperations"/>:
    /// read by a model or a script, never rendered as UI, so it is not localized — and its numbers
    /// are formatted invariantly, never off the process culture.
    /// </remarks>
    public static string TooLarge(long bytes)
        => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"Error: the file is {bytes} bytes; the upload ceiling is {MaxUploadBytes} bytes "
            + $"({MaxUploadBytes / (1024 * 1024)} MiB). Larger files need a streaming ingest path, not this tool.");
}
