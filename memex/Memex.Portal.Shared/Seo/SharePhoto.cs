using System.Net;
using System.Net.Sockets;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace Memex.Portal.Shared.Seo;

/// <summary>
/// 🚨 THE PHOTO CARD — a node's own authored picture, cut to a share card an unfurler will show.
///
/// <para><b>Why the portal re-serves another host's picture.</b> A social post's <c>mediaUrl</c> is
/// the print master: measured 2026-10-07 on memex's posts, 3720×2500 and 0.2–2.9 MB. WhatsApp renders
/// an EMPTY frame for an <c>og:image</c> much over ~300 KB, so the day posts began sharing their own
/// visual (<see cref="SeoResolver.ExtractImage"/> learned <c>mediaUrl</c>) every share showed a blank
/// picture. This fetches the master once per request and serves it, whole, inside
/// <see cref="OgCardRenderer.Width"/>×<see cref="OgCardRenderer.Height"/> JPEG, stepping the quality
/// down until it fits <see cref="TargetBytes"/>.</para>
///
/// <para><b>An anonymous route that fetches a URL is an SSRF surface, so the fetch is fenced.</b>
/// https only; the CONNECTION refuses any address that is not public unicast (loopback, private,
/// link-local, CGNAT, unique-local, multicast) — checked on the resolved address at connect time,
/// so neither a DNS answer nor a redirect can steer it inside; at most <see cref="MaxSourceBytes"/>
/// is read; the whole fetch is bounded by <see cref="FetchBudget"/>. The URL itself comes only from
/// the node's authored content, never from the request.</para>
///
/// <para>Reactive: the fetch is the one genuinely-async leaf and runs on the <c>Http</c>
/// <see cref="IIoPool"/>; decoding and encoding are synchronous Skia calls in the same leaf.</para>
/// </summary>
public static class SharePhoto
{
    /// <summary>The size an unfurler reliably shows; WhatsApp's practical ceiling is ~300 KB.</summary>
    public const int TargetBytes = 280 * 1024;

    /// <summary>The most a source picture may be — a print master is a few MB.</summary>
    public const long MaxSourceBytes = 20L * 1024 * 1024;

    /// <summary>The longest a fetch may take before the route falls back to the drawn card.</summary>
    public static readonly TimeSpan FetchBudget = TimeSpan.FromSeconds(15);

    private static readonly int[] Qualities = [85, 75, 65, 55, 45];

    // One fenced client for the process: the fence lives in its handler, which is immutable once built.
    private static readonly HttpClient Client = CreateClient();

    /// <summary>
    /// The photo card for <paramref name="sourceUrl"/>, or null when it cannot be served — not https,
    /// unreachable, refused by the fence, too large, or not a picture Skia can decode. Never faults:
    /// null is the answer the route turns into the drawn card.
    /// </summary>
    public static IObservable<byte[]?> Fetch(IMessageHub hub, string sourceUrl)
    {
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return System.Reactive.Linq.Observable.Return<byte[]?>(null);
        var pool = hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.Http) ?? IoPool.Unbounded;
        return System.Reactive.Linq.Observable.Catch(
            pool.Invoke(ct => FetchAsync(uri, ct)),
            (Exception _) => System.Reactive.Linq.Observable.Return<byte[]?>(null));
    }

    private static async Task<byte[]?> FetchAsync(Uri uri, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(FetchBudget);
        using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, budget.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxSourceBytes)
            return null;
        await using var stream = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, budget.Token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxSourceBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return Render(buffer.ToArray());
    }

    /// <summary>
    /// Fits <paramref name="source"/> (any format Skia decodes) whole into the card over a blurred fill, and encodes
    /// it as JPEG no larger than <see cref="TargetBytes"/> where any listed quality gets there; null
    /// when the bytes are not a picture. Pure — the part a test exercises without a network.
    /// </summary>
    public static byte[]? Render(byte[] source)
    {
        // SKBitmap.Decode THROWS on bytes no codec recognises (an HTML error page served as 200),
        // so ask for the codec first and answer null.
        using var codec = SKCodec.Create(new SKMemoryStream(source));
        if (codec is null)
            return null;
        using var decoded = SKBitmap.Decode(codec);
        if (decoded is null || decoded.Width == 0 || decoded.Height == 0)
            return null;

        const int width = OgCardRenderer.Width, height = OgCardRenderer.Height;
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White); // a transparent PNG must not turn black in JPEG
        var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
        using (var image = SKImage.FromBitmap(decoded))
        {
            // 🚨 CONTAIN, not cover: a post visual is usually a graphic whose headline sits at the
            // edge, and cover-cropping a 3:2 master to 1.91:1 cut the headline off (measured on
            // memex's InsuranceAiAdoptionGap chart). The whole picture is drawn; the bands it leaves
            // are filled with a blurred, cover-scaled copy of itself, so the card is never letterboxed
            // in a colour the picture does not have.
            using (var blur = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(40, 40, SKShaderTileMode.Clamp) })
                canvas.DrawImage(image, Fit(decoded.Width, decoded.Height, width, height, cover: true), sampling, blur);
            canvas.DrawImage(image, Fit(decoded.Width, decoded.Height, width, height, cover: false), sampling);
        }
        using var card = surface.Snapshot();

        byte[]? smallest = null;
        foreach (var quality in Qualities)
        {
            using var data = card.Encode(SKEncodedImageFormat.Jpeg, quality);
            smallest = data.ToArray();
            if (smallest.Length <= TargetBytes)
                break;
        }
        return smallest;
    }

    private static SKRect Fit(int sourceWidth, int sourceHeight, int width, int height, bool cover)
    {
        var sx = (float)width / sourceWidth;
        var sy = (float)height / sourceHeight;
        var scale = cover ? Math.Max(sx, sy) : Math.Min(sx, sy);
        var w = sourceWidth * scale;
        var h = sourceHeight * scale;
        return SKRect.Create((width - w) / 2, (height - h) / 2, w, h);
    }

    private static HttpClient CreateClient()
    {
        if (OperatingSystem.IsBrowser())
            return new HttpClient(); // never reached: the route runs on the portal server only
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            ConnectCallback = ConnectPublicOnly,
        };
        return new HttpClient(handler) { Timeout = FetchBudget };
    }

    private static async ValueTask<Stream> ConnectPublicOnly(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        if (OperatingSystem.IsBrowser())
            throw new PlatformNotSupportedException("The share photo fetch runs on the portal server only.");
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
        var address = addresses.FirstOrDefault(IsPublic)
                      ?? throw new HttpRequestException(
                          $"Share photo host '{context.DnsEndPoint.Host}' resolves to no public address.");
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Whether <paramref name="address"/> is public unicast — the only kind the photo fetch may
    /// connect to. Internal so the fence is pinned by a test rather than trusted.
    /// </summary>
    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            return false;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast
                     || address.IsIPv6UniqueLocal);
        var b = address.GetAddressBytes();
        return !(b[0] == 10                                   // 10/8
                 || b[0] == 0                                 // 0/8
                 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) // 172.16/12
                 || (b[0] == 192 && b[1] == 168)              // 192.168/16
                 || (b[0] == 169 && b[1] == 254)              // link-local, incl. cloud metadata
                 || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) // CGNAT 100.64/10
                 || b[0] >= 224);                             // multicast + reserved
    }
}
