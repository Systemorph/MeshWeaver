using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Reactive.Linq;
using System.Text.Json.Serialization;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;

namespace MeshWeaver.GitSync;

/// <summary>
/// The thin GitHub REST transport under <see cref="OctokitGitHubRepoClient"/>: one
/// <see cref="HttpClient"/>, System.Text.Json with GitHub's snake_case wire names, and exactly the
/// request/response fields the client reads or writes. It replaced the Octokit / Octokit.Reactive
/// packages, and keeps the parts of their behaviour the callers depend on:
///
/// <list type="bullet">
///   <item><b>Headers</b> — <c>User-Agent: MeshWeaver</c>, <c>Accept: application/vnd.github+json</c>,
///     <c>X-GitHub-Api-Version</c>, and <c>Authorization: token …</c> (Octokit's scheme for a token
///     credential). An EMPTY token sends no <c>Authorization</c> header at all: anonymous access to a
///     public repository, never "a bad token".</item>
///   <item><b>Redirects</b> are followed HERE, up to <see cref="MaxRedirects"/>, keeping the method
///     (except a 303, which becomes a body-less GET) — as Octokit's <c>RedirectHandler</c> did.
///     <see cref="HttpClient"/>'s own redirect following strips <c>Authorization</c> on every hop,
///     which would turn a RENAMED private repository (GitHub answers its old name with a 301) into
///     a 404.</item>
///   <item><b>The credential goes to the configured API ORIGIN and nowhere else</b> — scheme, host
///     AND port compared (<see cref="MayCarryCredential"/>), decided per request: on every redirect
///     hop and on every absolute pagination <c>Link</c>. A redirect to <c>http://</c> or to another
///     port of the same host, and a next-page URL on another host, are sent without it.</item>
///   <item><b>Pagination</b> — a listing follows <c>Link: rel="next"</c> to the last page, as
///     Octokit's <c>GetAll…</c> did; each page is its own pooled request, composed reactively.</item>
///   <item><b>Errors</b> — a non-success status becomes a <see cref="GitHubApiException"/> (typed
///     per status, see <see cref="Failure"/>); a transport fault stays the
///     <see cref="HttpRequestException"/> <see cref="HttpClient"/> threw.</item>
/// </list>
///
/// <para>🚨 <b>The surface is observable-only and owns the pool boundary.</b> Every public member
/// returns a cold <see cref="IObservable{T}"/> whose one HTTP exchange runs inside the HTTP
/// <see cref="IIoPool"/> (<see cref="IIoPool.Invoke{T}"/>); the only <c>async</c> code is the
/// PRIVATE leaf that pool invocation runs, so no caller can reach an unpooled await.</para>
/// </summary>
internal sealed class GitHubRestApi
{
    /// <summary>The REST API version every request pins.</summary>
    public const string ApiVersion = "2022-11-28";

    /// <summary>The most redirect hops one request follows before it fails — Octokit's
    /// <c>HttpClientAdapter</c> limit (it refused the fifth).</summary>
    public const int MaxRedirects = 4;

    /// <summary>The page size a listing asks for (GitHub's maximum).</summary>
    public const int PageSize = 100;

    private static readonly Uri DefaultBaseAddress = new("https://api.github.com/");

    private readonly HttpClient http;
    private readonly IoPoolRegistry ioPools;
    private readonly Uri baseAddress;

    /// <param name="http">The client to send with. Its handler should NOT follow redirects itself
    /// (see the type remarks); <see cref="CreateDefaultClient"/> builds one that does not.</param>
    /// <param name="ioPools">The pool registry; every exchange runs on its
    /// <see cref="IoPoolNames.Http"/> pool.</param>
    /// <param name="baseAddress">The API root; <c>https://api.github.com/</c> when null. Its origin
    /// is the only one a credential is ever sent to.</param>
    public GitHubRestApi(HttpClient http, IoPoolRegistry ioPools, Uri? baseAddress = null)
    {
        this.http = http;
        this.ioPools = ioPools;
        this.baseAddress = baseAddress ?? DefaultBaseAddress;
        Json = CreateJsonOptions();
    }

    /// <summary>The wire serializer options — snake_case names, nulls omitted on write, unknown
    /// fields ignored on read.</summary>
    public JsonSerializerOptions Json { get; }

    private IIoPool Pool => ioPools.Get(IoPoolNames.Http);

    /// <summary>The wire serializer options (a fresh instance per call).</summary>
    public static JsonSerializerOptions CreateJsonOptions() => new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>An <see cref="HttpClient"/> whose handler leaves redirects to this transport.</summary>
    public static HttpClient CreateDefaultClient()
        => new(new SocketsHttpHandler { AllowAutoRedirect = false });

    /// <summary>One request; the success body deserialized as <typeparamref name="T"/>.</summary>
    public IObservable<T> Send<T>(HttpMethod method, string path, string token, object? body = null)
        => SendRaw(method, path, token, body).Select(response => Deserialize<T>(response.Body, Json));

    /// <summary>A listing: every page (<c>Link: rel="next"</c>), as ONE emission.</summary>
    public IObservable<ImmutableList<T>> GetAll<T>(string path, string token)
        => GetAll<ImmutableList<T>, T>(path, token, page => page);

    /// <summary>A listing whose pages wrap the items in an envelope (e.g. <c>check_runs</c>).</summary>
    public IObservable<ImmutableList<TItem>> GetAll<TPage, TItem>(
        string path, string token, Func<TPage, IEnumerable<TItem>?> items)
        => Page(WithQuery(path, "per_page=" + PageSize.ToString(CultureInfo.InvariantCulture)), token, items);

    /// <summary>One page, then — when GitHub names a next one — the rest, each its own pooled
    /// request. The next-page URL is absolute and may name ANY host; the credential decision is
    /// re-made for it in <see cref="MayCarryCredential"/>, never inherited.</summary>
    private IObservable<ImmutableList<TItem>> Page<TPage, TItem>(
        string url, string token, Func<TPage, IEnumerable<TItem>?> items)
        => SendRaw(HttpMethod.Get, url, token, null).SelectMany(response =>
        {
            var here = (items(Deserialize<TPage>(response.Body, Json)) ?? []).ToImmutableList();
            return NextPage(response.Link) is { } next
                ? Page(next, token, items).Select(rest => here.AddRange(rest))
                : Observable.Return(here);
        });

    /// <summary>A success answer: its body and its <c>Link</c> header (null when absent).</summary>
    internal sealed record RawResponse(HttpStatusCode Status, string Body, string? Link);

    /// <summary>
    /// One request with redirects followed (see the type remarks), as one pooled exchange. A
    /// non-success final status is thrown as a <see cref="GitHubApiException"/>; only a 2xx emits.
    /// </summary>
    public IObservable<RawResponse> SendRaw(HttpMethod method, string path, string token, object? body)
        => Pool.Invoke(ct => SendRawLeafAsync(method, path, token, body, ct));

    /// <summary>
    /// Whether a request to <paramref name="target"/> may carry the credential: only when it is the
    /// configured API origin — same scheme, same host, same port. A plaintext <c>http://</c>
    /// redirect, another port on the same host, or another host entirely never receives it.
    /// </summary>
    internal bool MayCarryCredential(Uri target)
        => Uri.Compare(target, baseAddress, UriComponents.SchemeAndServer, UriFormat.Unescaped,
               StringComparison.OrdinalIgnoreCase) == 0;

    // The ONE async leaf, run solely by Pool.Invoke above (private: nothing else can reach it).
    private async Task<RawResponse> SendRawLeafAsync(
        HttpMethod method, string path, string token, object? body, CancellationToken ct)
    {
        var uri = Resolve(path);
        var json = body is null ? null : JsonSerializer.Serialize(body, body.GetType(), Json);
        var hasToken = !string.IsNullOrEmpty(token);
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("MeshWeaver", null));
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);
            // Decided per hop against the configured origin — never carried over from the hop before.
            if (hasToken && MayCarryCredential(uri))
                request.Headers.Authorization = new AuthenticationHeaderValue("token", token);
            if (json is not null)
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
            {
                if (hop + 1 > MaxRedirects)
                    throw new InvalidOperationException(
                        $"GitHub redirected {method} {path} more than {MaxRedirects} times.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (response.StatusCode == HttpStatusCode.SeeOther)
                {
                    method = HttpMethod.Get;
                    json = null;
                }
                continue;
            }

            if (!response.IsSuccessStatusCode)
                throw Failure(response.StatusCode, text, response.Headers);

            return new RawResponse(
                response.StatusCode, text,
                response.Headers.TryGetValues("Link", out var links) ? string.Join(",", links) : null);
        }
    }

    /// <summary>Deserializes a GitHub body with the wire options. An empty body reads as
    /// <c>default</c> only for a nullable target; otherwise it is a malformed answer.</summary>
    public static T Deserialize<T>(string body, JsonSerializerOptions json)
        => JsonSerializer.Deserialize<T>(body.Length == 0 ? "null" : body, json)
           ?? throw new JsonException($"GitHub answered an empty body where {typeof(T).Name} was expected.");

    /// <summary>
    /// A non-success status as the typed exception: 401 →
    /// <see cref="GitHubAuthorizationException"/>; 403 → <see cref="GitHubRateLimitExceededException"/>
    /// when the budget is exhausted or GitHub's message names a rate limit, otherwise
    /// <see cref="GitHubForbiddenException"/>; 404 → <see cref="GitHubNotFoundException"/>; 422 →
    /// <see cref="GitHubValidationException"/>; 429 → <see cref="GitHubRateLimitExceededException"/>;
    /// anything else (409 "Git Repository is empty.", 405 "not mergeable", 5xx) → a plain
    /// <see cref="GitHubApiException"/> carrying the status.
    /// </summary>
    internal static GitHubApiException Failure(HttpStatusCode status, string body, HttpResponseHeaders? headers)
    {
        var (message, documentationUrl, errors) = ErrorBody(body);
        message ??= $"GitHub answered HTTP {(int)status} ({status}).";
        var remaining = Header(headers, "x-ratelimit-remaining");
        var reset = long.TryParse(Header(headers, "x-ratelimit-reset"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch)
            ? DateTimeOffset.FromUnixTimeSeconds(epoch)
            : (DateTimeOffset?)null;
        var rateLimited = remaining == "0" || message.Contains("rate limit", StringComparison.OrdinalIgnoreCase);
        return status switch
        {
            HttpStatusCode.Unauthorized => new GitHubAuthorizationException(message, documentationUrl),
            HttpStatusCode.Forbidden when rateLimited => new GitHubRateLimitExceededException(status, message, reset, documentationUrl),
            HttpStatusCode.Forbidden => new GitHubForbiddenException(message, documentationUrl),
            HttpStatusCode.NotFound => new GitHubNotFoundException(message, documentationUrl),
            HttpStatusCode.UnprocessableEntity => new GitHubValidationException(message, errors, documentationUrl),
            HttpStatusCode.TooManyRequests => new GitHubRateLimitExceededException(status, message, reset, documentationUrl),
            _ => new GitHubApiException(status, message, documentationUrl),
        };
    }

    private static (string? Message, string? DocumentationUrl, string? Errors) ErrorBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return (null, null, null);
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return (null, null, null);
            var root = doc.RootElement;
            var message = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() : null;
            var docs = root.TryGetProperty("documentation_url", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() : null;
            var errors = root.TryGetProperty("errors", out var e) ? e.GetRawText() : null;
            return (string.IsNullOrEmpty(message) ? null : message, docs, errors);
        }
        catch (JsonException)
        {
            // A proxy page or other non-JSON body: no GitHub message to carry.
            return (null, null, null);
        }
    }

    private static string? Header(HttpResponseHeaders? headers, string name)
        => headers is not null && headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private Uri Resolve(string pathOrUrl)
        => Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var absolute) && absolute.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? absolute
            : new Uri(baseAddress, pathOrUrl.TrimStart('/'));

    private static string WithQuery(string path, string query)
        => path + (path.Contains('?') ? "&" : "?") + query;

    /// <summary>The <c>rel="next"</c> target of an RFC 8288 <c>Link</c> header; null on the last page.</summary>
    internal static string? NextPage(string? link)
    {
        if (string.IsNullOrEmpty(link))
            return null;
        foreach (var part in link.Split(','))
        {
            var segments = part.Split(';');
            if (segments.Length < 2)
                continue;
            if (!segments.Skip(1).Any(s => s.Trim().Equals("rel=\"next\"", StringComparison.OrdinalIgnoreCase)))
                continue;
            var target = segments[0].Trim();
            if (target.StartsWith('<') && target.EndsWith('>'))
                return target[1..^1];
        }
        return null;
    }

    /// <summary>One path segment, escaped (a branch name's own <c>/</c> separators are kept by
    /// escaping each segment of it separately — see <see cref="EscapePath"/>).</summary>
    public static string Escape(string segment) => Uri.EscapeDataString(segment);

    /// <summary>A multi-segment value (a branch name, a file path) escaped per segment, slashes kept.</summary>
    public static string EscapePath(string path)
        => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
}

// ══════════════════════════════════════════════════════════════════════════
//  Wire records — exactly the fields the client reads or writes.
// ══════════════════════════════════════════════════════════════════════════

internal sealed record GitHubWireUser(string? Login);

internal sealed record GitHubWireRepository(string? Name, GitHubWireUser? Owner, string? DefaultBranch);

internal sealed record GitHubWireNewRepository(string Name, bool Private, bool AutoInit);

internal sealed record GitHubWireBranch(string? Name);

internal sealed record GitHubWireObject(string Sha);

internal sealed record GitHubWireReference(string? Ref, GitHubWireObject Object);

internal sealed record GitHubWireNewReference(string Ref, string Sha);

internal sealed record GitHubWireReferenceUpdate(string Sha, bool Force);

internal sealed record GitHubWireCommit(string Sha, GitHubWireObject Tree);

internal sealed record GitHubWireSignature(string Name, string Email, string Date);

internal sealed record GitHubWireNewCommit(
    string Message, string Tree, IReadOnlyList<string> Parents,
    GitHubWireSignature Author, GitHubWireSignature Committer);

internal sealed record GitHubWireTreeItem(string Path, string? Type, string Sha);

internal sealed record GitHubWireTree(string? Sha, IReadOnlyList<GitHubWireTreeItem>? Tree, bool Truncated);

internal sealed record GitHubWireNewTreeItem(string Path, string Mode, string Type, string Sha);

internal sealed record GitHubWireNewTree(IReadOnlyList<GitHubWireNewTreeItem> Tree);

internal sealed record GitHubWireBlob(string Content, string? Encoding);

internal sealed record GitHubWireNewBlob(string Content, string Encoding);

internal sealed record GitHubWireCreated(string Sha);

internal sealed record GitHubWireCreateFile(string Message, string Content, string Branch);

internal sealed record GitHubWireCompareFile(string? Filename, string? PreviousFilename);

internal sealed record GitHubWireCompare(string? Status, IReadOnlyList<GitHubWireCompareFile>? Files);

internal sealed record GitHubWireLabel(string? Name);

/// <summary>GitHub's <c>issue</c> object — the REST answer and the webhook payload share the schema.</summary>
internal sealed record GitHubWireIssue(
    int Number,
    string? Title,
    string? Body,
    string? State,
    string? StateReason,
    GitHubWireUser? User,
    IReadOnlyList<GitHubWireLabel>? Labels,
    IReadOnlyList<GitHubWireUser>? Assignees,
    int Comments,
    string? HtmlUrl,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? ClosedAt,
    JsonElement? PullRequest);

internal sealed record GitHubWireNewIssue(string Title, string Body, IReadOnlyList<string> Labels);

internal sealed record GitHubWireIssueUpdate(string State);

internal sealed record GitHubWireComment(
    long Id, GitHubWireUser? User, string? Body, DateTimeOffset? CreatedAt, string? HtmlUrl);

internal sealed record GitHubWireNewComment(string Body);

internal sealed record GitHubWireBranchRef(string? Ref, string? Sha);

internal sealed record GitHubWirePullRequest(
    int Number,
    string? Title,
    string? Body,
    string? State,
    bool Merged,
    DateTimeOffset? MergedAt,
    bool Draft,
    GitHubWireUser? User,
    GitHubWireBranchRef? Head,
    GitHubWireBranchRef? Base,
    string? HtmlUrl,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    bool? Mergeable,
    string? MergeableState,
    int Comments);

internal sealed record GitHubWireNewPullRequest(string Title, string Head, string Base, string Body);

internal sealed record GitHubWireReview(GitHubWireUser? User, string? State, DateTimeOffset? SubmittedAt);

internal sealed record GitHubWireCheckRun(string? Status, string? Conclusion);

internal sealed record GitHubWireCheckRuns(IReadOnlyList<GitHubWireCheckRun>? CheckRuns);

internal sealed record GitHubWireMerge(string? CommitTitle, string? CommitMessage, string MergeMethod);

internal sealed record GitHubWireMergeResult(bool Merged, string? Sha, string? Message);
