using System.Reactive.Linq;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Markdown.Export.Branding;

/// <summary>
/// Resolves a brand node path to a fully-hydrated <see cref="BrandingOptions"/>
/// ready for the document renderer.
/// Cascade: <c>CorporateIdentity</c> node -> <c>Organization</c>-shaped content (logo-only) ->
/// raw content path (logo-only) -> portal defaults.
/// Reactive — public API returns <c>IObservable&lt;T&gt;</c>; no <c>Task&lt;T&gt;</c>.
/// </summary>
public class BrandingResolver(IMessageHub hub, ExportTemplateResolver templateResolver, ILogger<BrandingResolver> logger)
{
    /// <summary>
    /// Resolves the branding for a given path. Returns <see cref="BrandingOptions.Default"/> on any miss.
    /// </summary>
    public IObservable<BrandingOptions> Resolve(string? brandNodePath)
    {
        if (string.IsNullOrWhiteSpace(brandNodePath))
            return Observable.Return(BrandingOptions.Default);

        // Raw content path (image): treat as logo-only brand.
        if (brandNodePath.StartsWith("content:", StringComparison.OrdinalIgnoreCase))
        {
            return LoadLogo(brandNodePath)
                .Select(logo => BrandingOptions.Default with { Logo = logo });
        }

        return hub.GetWorkspace().GetMeshNodeStream(brandNodePath)
            .Select(n => (MeshNode?)n)
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(10))
            .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null))
            .SelectMany(node =>
            {
                if (node is null)
                {
                    logger.LogWarning("Brand node '{Path}' not found; using portal defaults", brandNodePath);
                    return Observable.Return(BrandingOptions.Default);
                }

                return node.NodeType switch
                {
                    CorporateIdentityNodeType.NodeType when node.Content is CorporateIdentity ci
                        => FromCorporateIdentity(ci),
                    "Organization"
                        => FromOrganization(node),
                    _ => LoadLogo(node.Icon).Select(logo => BrandingOptions.Default with
                    {
                        Name = node.Name ?? "",
                        Logo = logo
                    })
                };
            });
    }

    private IObservable<BrandingOptions> FromCorporateIdentity(CorporateIdentity ci)
    {
        var logoObs = LoadLogo(ci.LogoPath);
        var templateObs = Observable.FromAsync(ct => templateResolver.LoadAsync(ci.TemplatePath, ct));

        return logoObs.Zip(templateObs, (logo, template) => new BrandingOptions
        {
            Name = ci.Name ?? ci.Id,
            Tagline = ci.Tagline ?? "",
            Logo = logo ?? template?.Logo,
            PrimaryColor = ci.PrimaryColor ?? BrandingOptions.Default.PrimaryColor,
            AccentColor = ci.AccentColor ?? BrandingOptions.Default.AccentColor,
            FontFamily = ci.FontFamily ?? template?.FontFamily ?? BrandingOptions.Default.FontFamily,
            HeaderText = ci.HeaderText ?? "",
            FooterText = ci.FooterText ?? "",
            Website = ci.Website ?? "",
            TemplateDocxBytes = template?.DocxBytes
        });
    }

    private IObservable<BrandingOptions> FromOrganization(MeshNode node)
    {
        // Organization content is untyped JSON at present; read known fields via reflection-friendly dynamic path.
        // We only need logo and name. Everything else falls back to defaults.
        string? logoPath = node.Icon;
        string name = node.Name ?? "";

        if (node.Content is System.Text.Json.Nodes.JsonObject jo)
        {
            if (jo["logo"]?.ToString() is { Length: > 0 } l) logoPath ??= l;
            if (jo["name"]?.ToString() is { Length: > 0 } n) name = n;
        }

        return LoadLogo(logoPath).Select(logo => BrandingOptions.Default with
        {
            Name = name,
            Logo = logo
        });
    }

    // File-I/O kernel kept as async Task internally — wrapped into IObservable at the
    // single boundary below. This is the "non-hub I/O" exception per the reactive rules.
    private IObservable<LogoImage?> LoadLogo(string? path) =>
        Observable.FromAsync(ct => LoadLogoInternalAsync(path, ct));

    private async Task<LogoImage?> LoadLogoInternalAsync(string? path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            // content:... paths go through the content service.
            if (path.StartsWith("content:", StringComparison.OrdinalIgnoreCase))
            {
                var rel = path["content:".Length..];
                var (collection, subPath) = SplitCollection(rel);
                var contentSvc = hub.ServiceProvider.GetService<IContentService>();
                if (contentSvc is null) return null;
                await using var stream = await contentSvc.GetContentAsync(collection, subPath, ct);
                if (stream is null) return null;
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);
                return new LogoImage(ms.ToArray(), InferMime(path));
            }

            // Portal-relative paths like /static/storage/content/Systemorph/logo_t.png
            // are served by the host; for the export we resolve them via the content service
            // when they follow the conventional /static/storage/content/{collection}/{path} shape.
            const string staticPrefix = "/static/storage/content/";
            if (path.StartsWith(staticPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var rel = path[staticPrefix.Length..];
                var (collection, subPath) = SplitCollection(rel);
                var contentSvc = hub.ServiceProvider.GetService<IContentService>();
                if (contentSvc is null) return null;
                await using var stream = await contentSvc.GetContentAsync(collection, subPath, ct);
                if (stream is null) return null;
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, ct);
                return new LogoImage(ms.ToArray(), InferMime(path));
            }

            // Unsupported path shape — either an absolute URL or a relative path we can't resolve server-side.
            if (Uri.TryCreate(path, UriKind.Absolute, out _))
                logger.LogInformation("Logo path '{Path}' is an absolute URL; skipping.", path);
            else
                logger.LogInformation("Logo path '{Path}' is an unsupported relative path; skipping.", path);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load logo '{Path}'; continuing without", path);
            return null;
        }
    }

    private static (string Collection, string Path) SplitCollection(string rel)
    {
        rel = rel.Replace('\\', '/').TrimStart('/');
        var slash = rel.IndexOf('/');
        return slash < 0 ? (rel, "") : (rel[..slash], rel[(slash + 1)..]);
    }

    private static string InferMime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => "application/octet-stream"
    };
}
