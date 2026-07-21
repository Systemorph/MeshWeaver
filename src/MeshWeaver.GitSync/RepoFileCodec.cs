using System.Text;

namespace MeshWeaver.GitSync;

/// <summary>
/// The ONE text-vs-binary classification for repo file bytes, shared by every transport
/// (<see cref="OctokitGitHubRepoClient"/>'s blob decode, <see cref="GitProtocolRepoClient"/>'s
/// worktree read). Valid UTF-8 becomes a text <see cref="RepoFile"/> (so node/markdown parsing is
/// unchanged); anything else carries its raw bytes in <see cref="RepoFile.Binary"/> — round-tripping
/// arbitrary bytes through a UTF-8 string corrupts them (the bug that repeatedly nuked the course
/// videos).
/// </summary>
internal static class RepoFileCodec
{
    private static readonly Encoding StrictUtf8 =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>A <see cref="RepoFile"/> from raw bytes: text when valid UTF-8, binary otherwise.</summary>
    public static RepoFile FromBytes(string path, byte[] bytes) =>
        TryDecodeUtf8(bytes, out var text)
            ? new RepoFile(path, text)
            : new RepoFile(path, string.Empty, bytes);

    /// <summary>
    /// True + the decoded text when <paramref name="bytes"/> is valid UTF-8; false for binary
    /// content. Uses a strict (throw-on-invalid) decoder so an arbitrary byte stream (a video,
    /// a font) is classified binary rather than silently lossily decoded.
    /// </summary>
    public static bool TryDecodeUtf8(byte[] bytes, out string text)
    {
        try
        {
            text = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }
}
