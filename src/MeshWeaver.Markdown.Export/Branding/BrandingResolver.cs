using System.Reactive.Linq;
using MeshWeaver.ContentCollections;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Markdown.Export.Branding;

/// <summary>
/// Resolves a brand node path to a fully-hydrated <see cref="BrandingOptions"/>
/// ready for the document renderer.
/// Cascade: <c>CorporateIdentity</c> node -> <c>Organization</c>-shaped content (logo-only) ->
/// raw content path (logo-only) -> portal defaults.
/// </summary>
public class BrandingResolver(IMessageHub hub, ILogger<BrandingResolver> logger)
{
    /// <summary>
    /// Resolves the branding for a given path. Returns <see cref="BrandingOptions.Default"/> on any miss.
    /// </summary>
    public async Task<BrandingOptions> ResolveAsync(string? brandNodePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(brandNodePath))
            return BrandingOptions.Default;

        // Raw content path (image): treat as logo-only brand.
        if (brandNodePath.StartsWith("content:", StringComparison.OrdinalIgnoreCase))
        {
            var logo = await LoadLogoAsync(brandNodePath, ct);
            return BrandingOptions.Default with { Logo = logo };
        }

        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var node = await meshService.QueryAsync<MeshNode>($"path:{brandNodePath}").FirstOrDefaultAsync();
        if (node is null)
        {
            logger.LogWarning("Brand node '{Path}' not found; using portal defaults", brandNodePath);
            return BrandingOptions.Default;
        }

        return node.NodeType switch
        {
            CorporateIdentityNodeType.NodeType when node.Content is CorporateIdentity ci
                => await FromCorporateIdentityAsync(ci, ct),
            "Organization"
                => await FromOrganizationAsync(node, ct),
            _ => BrandingOptions.Default with
            {
                Name = node.Name ?? "",
                Logo = await LoadLogoAsync(node.Icon, ct)
            }
        };
    }

    private async Task<BrandingOptions> FromCorporateIdentityAsync(CorporateIdentity ci, CancellationToken ct)
    {
        var logo = await LoadLogoAsync(ci.LogoPath, ct);
        return new BrandingOptions
        {
            Name = ci.Name ?? ci.Id,
            Tagline = ci.Tagline ?? "",
            Logo = logo,
            PrimaryColor = ci.PrimaryColor ?? BrandingOptions.Default.PrimaryColor,
            AccentColor = ci.AccentColor ?? BrandingOptions.Default.AccentColor,
            FontFamily = ci.FontFamily ?? BrandingOptions.Default.FontFamily,
            HeaderText = ci.HeaderText ?? "",
            FooterText = ci.FooterText ?? "",
            Website = ci.Website ?? ""
        };
    }

    private async Task<BrandingOptions> FromOrganizationAsync(MeshNode node, CancellationToken ct)
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

        return BrandingOptions.Default with
        {
            Name = name,
            Logo = await LoadLogoAsync(logoPath, ct)
        };
    }

    private async Task<LogoImage?> LoadLogoAsync(string? path, CancellationToken ct)
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

            // Absolute URL — not supported for server-side embedding (no outgoing HTTP here by design).
            logger.LogInformation("Logo path '{Path}' is an absolute URL; skipping.", path);
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
