using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Portal.Shared.Seo;

/// <summary>
/// What the crawler-facing head needs to know about the requested page: the resolved node and
/// the pieces of its content the meta tags are built from. Only ever produced for pages an
/// ANONYMOUS visitor may read (the <see cref="AnonymousGate"/> decision) — a private node's
/// name/description must never leak into markup served to a logged-out crawler.
/// </summary>
public sealed record SeoPageData(MeshNode Node, string? Description, string? Image)
{
    /// <summary>The node's pre-rendered markdown body, when it carries one — served inside
    /// <c>&lt;noscript&gt;</c> so non-JS crawlers index the actual page content.</summary>
    public string? PreRenderedHtml => Node.PreRenderedHtml;
}

/// <summary>
/// Server-side SEO resolution for the initial HTTP response. Reactive end to end; the ONE
/// <c>Task</c> bridge sits at the Razor static-SSR boundary (<see cref="ResolveAsync"/>), the
/// same adapter shape the MCP/REST surfaces use. Fail-open to null: a slow or faulted mesh
/// never delays page delivery — the page just ships the generic head.
/// </summary>
public static class SeoResolver
{
    /// <summary>Per-request stash key so the head and body components resolve ONCE.</summary>
    public const string HttpContextItem = "Memex.Seo.PageData";

    /// <summary>Route prefixes that are never mesh nodes — skipped without touching the mesh.</summary>
    private static readonly string[] NonNodePrefixes =
        ["login", "api", "_blazor", "_framework", "_content", "dev", "mcp", "static", "webhooks"];

    /// <summary>Whether the request path can be a node page worth resolving.</summary>
    public static bool IsCandidatePath(string? path)
    {
        var trimmed = (path ?? "").Trim('/');
        if (trimmed.Length == 0)
            return false;
        var first = trimmed.Split('/')[0];
        return !NonNodePrefixes.Any(p => string.Equals(p, first, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Resolves the request path to its node and gates it through
    /// <see cref="AnonymousGate.AllowAnonymous"/>. Emits null when the path is no node, the node
    /// is not anonymous-readable, or anything errors/times out. Cold.
    /// </summary>
    public static IObservable<SeoPageData?> Resolve(IMessageHub hub, string path)
    {
        var resolver = hub.ServiceProvider.GetService<IPathResolver>();
        if (resolver is null)
            return Observable.Return<SeoPageData?>(null);
        return resolver.ResolvePath(path.Trim('/'))
            .Take(1)
            .SelectMany(resolution => resolution?.Node is not { } node
                ? Observable.Return<SeoPageData?>(null)
                : AnonymousGate.AllowAnonymous(hub, resolution.Prefix)
                    .Take(1)
                    .Select(allowed => allowed
                        ? new SeoPageData(node, ExtractDescription(node), ExtractImage(node))
                        : null))
            .Timeout(TimeSpan.FromSeconds(3))
            .Catch<SeoPageData?, Exception>(_ => Observable.Return<SeoPageData?>(null));
    }

    /// <summary>The static-SSR boundary bridge — the only <c>Task</c> on this surface.</summary>
    public static Task<SeoPageData?> ResolveAsync(IMessageHub hub, string path) =>
        System.Reactive.Threading.Tasks.TaskObservableExtensions.ToTask(
            Resolve(hub, path).FirstAsync());

    /// <summary>
    /// The page description for meta tags: the node's Description, else the content's
    /// <c>abstract</c>/<c>description</c> member (untyped — content arrives as JSON here).
    /// </summary>
    public static string? ExtractDescription(MeshNode node) =>
        FirstNonEmpty(
            node.Description,
            ContentString(node, "abstract"),
            ContentString(node, "description"));

    /// <summary>The share image for og:image: the content's <c>poster</c> (store plugins) or
    /// <c>thumbnail</c> (markdown pages). Root-relative or absolute URLs only.</summary>
    public static string? ExtractImage(MeshNode node)
    {
        var candidate = FirstNonEmpty(ContentString(node, "poster"), ContentString(node, "thumbnail"));
        return candidate is not null
            && (candidate.StartsWith('/') || candidate.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            ? candidate
            : null;
    }

    /// <summary>A string member of the node's content, read untyped.</summary>
    public static string? ContentString(MeshNode node, string property) =>
        node.Content is JsonElement { ValueKind: JsonValueKind.Object } je
            && je.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>A decimal member of the node's content, read untyped.</summary>
    public static decimal? ContentDecimal(MeshNode node, string property) =>
        node.Content is JsonElement { ValueKind: JsonValueKind.Object } je
            && je.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            ? value.GetDecimal()
            : null;

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
