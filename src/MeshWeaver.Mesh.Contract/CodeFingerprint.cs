using System.Security.Cryptography;
using System.Text;

namespace MeshWeaver.Mesh;

/// <summary>
/// Stable fingerprint of an executable cell's source, used to answer one question a notebook cell
/// must answer honestly: <em>has the code changed since the run whose output I am showing?</em>
/// Stamped onto <see cref="CodeConfiguration.LastExecutedCodeHash"/> at submit time and re-computed
/// from the node's current content at render time; a mismatch means the visible output belongs to
/// code the reader is no longer looking at.
/// </summary>
/// <remarks>
/// 🚨 Cryptographic, deliberately — NOT <c>string.GetHashCode()</c>. String hash codes are randomized
/// per process in .NET, so a persisted one compares unequal after the next pod restart and EVERY cell
/// would read as stale forever. SHA-256 over the normalized source is stable across processes,
/// machines and framework versions.
/// </remarks>
public static class CodeFingerprint
{
    /// <summary>
    /// The fingerprint of <paramref name="code"/> in <paramref name="language"/>: a base64 SHA-256 over
    /// the language and the line-ending-normalized source. The language participates because it decides
    /// where the code RUNS (in-process Roslyn vs. a foreign-language worker) — switching it makes the
    /// previous output stale just as surely as editing a line.
    /// </summary>
    /// <param name="code">The cell's source; null is treated as empty.</param>
    /// <param name="language">The cell's language; null/blank is treated as <c>csharp</c> (the default
    /// everywhere else in the code path, so the fingerprint must agree).</param>
    /// <returns>A base64 fingerprint, stable across processes.</returns>
    public static string Of(string? code, string? language)
    {
        var lang = string.IsNullOrWhiteSpace(language) ? "csharp" : language;
        // '\n' separator: a language can't contain one, so no (language, code) pair can collide with
        // another by shifting the boundary.
        var payload = Encoding.UTF8.GetBytes($"{lang}\n{Normalize(code)}");
        return Convert.ToBase64String(SHA256.HashData(payload));
    }

    /// <summary>
    /// Line-ending- and trailing-whitespace-normalized source. An editor that rewrites CRLF↔LF, or a
    /// save that adds a trailing newline, must not read as "the code changed" — that would cry wolf and
    /// train readers to ignore the indicator.
    /// </summary>
    internal static string Normalize(string? code) =>
        (code ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();
}
