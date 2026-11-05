using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// Where a Code node's source file lives in its repository: the repository, the exact ref the
/// mesh copy was materialised from, and the file's repo-relative path. Produced by an
/// <see cref="ICodeSourceFiles"/> from the module's own recorded provenance — the
/// <c>{Space}/_GitSync</c> entry or the <c>Plugins/{id}</c> install record — never guessed.
/// </summary>
/// <param name="RepositoryUrl">The repository, e.g. <c>https://github.com/acme/modules</c>.</param>
/// <param name="Ref">The commit (or, where only that is recorded, the branch) the mesh copy came from.</param>
/// <param name="FilePath">The file's path inside the repository, slash-separated, no leading slash.</param>
public sealed record CodeSourceOrigin(string RepositoryUrl, string Ref, string FilePath)
{
    /// <summary>The file's page on the hosting service (<c>{repo}/blob/{ref}/{path}</c>).</summary>
    public string BrowseUrl =>
        $"{RepositoryUrl.TrimEnd('/')}/blob/{Uri.EscapeDataString(Ref)}/"
        + string.Join('/', FilePath.Split('/').Select(Uri.EscapeDataString));

    /// <summary>A short label for the ref: a commit sha shortened to 7 characters, a branch as written.</summary>
    public string RefLabel =>
        Ref is { Length: 40 or 64 } && Ref.All(char.IsAsciiHexDigit) ? Ref[..7] : Ref;
}

/// <summary>
/// The outcomes of a <see cref="ICodeSourceFiles.Read"/> — an OPEN vocabulary of string constants
/// (policy <c>open-vocabulary-string-constants</c>), so a reader may add one without a wire break.
/// </summary>
public static class CodeSourceOutcomes
{
    /// <summary>The file was read from the repository with the viewer's own credential.</summary>
    public const string Read = "Read";

    /// <summary>The node has no recorded repository origin (it was authored in the mesh, or its
    /// module's provenance names no repository). The mesh copy is all there is.</summary>
    public const string NoOrigin = "NoOrigin";

    /// <summary>The viewer has connected no GitHub account, so the private file cannot be read
    /// AS THEM. The page asks them to sign in with GitHub.</summary>
    public const string SignInRequired = "SignInRequired";

    /// <summary>GitHub refused the viewer's credential the file (401 / 403 / 404 — GitHub answers a
    /// private repository the viewer cannot see with 404). The page asks them to sign in again.</summary>
    public const string Refused = "Refused";

    /// <summary>The read failed for another reason (rate limit, network, a 5xx).</summary>
    public const string Failed = "Failed";
}

/// <summary>One answer of <see cref="ICodeSourceFiles.Read"/>.</summary>
/// <param name="Outcome">One of <see cref="CodeSourceOutcomes"/>.</param>
/// <param name="Origin">Where the file lives; null only for <see cref="CodeSourceOutcomes.NoOrigin"/>.</param>
/// <param name="Text">The file's text; set only for <see cref="CodeSourceOutcomes.Read"/>.</param>
/// <param name="StatusCode">The HTTP status GitHub answered, for <see cref="CodeSourceOutcomes.Refused"/>
/// and, where there was one, <see cref="CodeSourceOutcomes.Failed"/>.</param>
public sealed record CodeSourceRead(
    string Outcome,
    CodeSourceOrigin? Origin = null,
    string? Text = null,
    int? StatusCode = null)
{
    /// <summary>The answer for a node with no repository origin.</summary>
    public static CodeSourceRead WithoutOrigin { get; } = new(CodeSourceOutcomes.NoOrigin);
}

/// <summary>
/// Reads a Code node's source file FROM ITS REPOSITORY, as the viewer — the NodeType page's
/// <c>Code</c> area shows that file (maintainer, 2026-11-05: "it's just about getting the file from
/// github and displaying on gui"; "if i am not authorized, ask for login").
///
/// <para>🚨 <b>The viewer's own credential, never a server one.</b> The repository is usually
/// private. An implementation authenticates the read with the VIEWER's connected GitHub account
/// and nothing else — never the instance's App installation token, never a source's configured
/// token — so the page can never show a person a file they could not open on GitHub themselves. A
/// viewer without a connected account gets <see cref="CodeSourceOutcomes.SignInRequired"/>.</para>
///
/// <para>Optional: with none registered the Code area shows the mesh copy, as it always did.</para>
/// </summary>
public interface ICodeSourceFiles
{
    /// <summary>
    /// The file behind <paramref name="codeNodePath"/>, read as <paramref name="viewer"/>. Cold and
    /// LIVE: emits an answer, and a new one whenever the viewer's GitHub connection changes (a page
    /// returning from the sign-in shows the file without a reload). Never faults: a failed read is a
    /// <see cref="CodeSourceOutcomes.Failed"/> answer the page can render, carrying the origin when it
    /// was known.
    /// </summary>
    /// <param name="codeNodePath">The Code node's mesh path.</param>
    /// <param name="viewer">The identity the page renders for; null or virtual = signed out.</param>
    IObservable<CodeSourceRead> Read(string codeNodePath, AccessContext? viewer);
}
