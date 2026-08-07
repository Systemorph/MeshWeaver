using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Blazor;

/// <summary>
/// The access decision for the <c>/static/**</c> content endpoint (issue #587).
///
/// <para><b>The hole this closes.</b> <c>MapStaticContent</c> resolved a collection and streamed
/// the file with no authorization anywhere in <c>ResolveStatic</c> → <c>ServeFromCollection</c> →
/// <c>ServeFile</c>. Both URL shapes leaked: <c>/static/storage/content/{node}/{file}</c> read the
/// mesh-level backing store directly (every partition's uploads, attachments, PDFs — flat and
/// world-readable), and <c>/static/{address}/{collection}/{file}</c> resolved ANY address and
/// served that hub's collections without ever consulting the partition's policy. "The URL is
/// unguessable" was the only protection, and the scheme is predictable.</para>
///
/// <para><b>The decision.</b> Exactly the one the ordinary content read makes. A file is OWNED by
/// the mesh node whose collection it lives in; reading it through
/// <c>/content/{address}/{collection}/{file}</c> posts a <c>GetDataRequest</c>, which carries
/// <c>[RequiresPermission(Permission.Read)]</c>, and <c>AccessControlPipeline</c> checks that Read
/// against the OWNING HUB'S OWN PATH. This gate checks the same thing: <c>Read</c> on the owning
/// node, folded by the same <c>PermissionEvaluator</c> (via <c>hub.GetEffectivePermissions</c>) —
/// so partition scoping, group expansion, <c>PartitionAccessPolicy.PublicRead</c>, and the
/// paywall's per-subject deny/allow assignments all apply verbatim. No parallel rule set.</para>
///
/// <para><b>Attribution is the deepest node, not the partition root.</b> The owner candidate is run
/// through <see cref="IPathResolver"/>, which returns the LONGEST path prefix that is a real mesh
/// node. Where content mirrors the node tree (the <c>content:</c> convention) a paid lesson's media
/// under <c>{Course}/{Lesson}/…</c> is therefore attributed to <c>{Course}/{Lesson}</c> and the
/// lesson's <c>Public</c>/<c>Anonymous</c> deny applies — while the cover under <c>{Course}/…</c>
/// stays anonymous. Read folds hierarchically, so checking the deepest node is never weaker than
/// checking its ancestors.</para>
///
/// <para><b>Fail closed, with one deliberate exception.</b> No owner ⇒ deny. Resolution or
/// permission failure ⇒ deny. The exception is a mesh with no
/// <see cref="EffectivePermissionsDelegate"/> registered (RLS not installed): there the canonical
/// read path does not check either — <c>AccessControlPipeline</c> invokes <c>next</c> outright when
/// the delegate is absent — so allowing matches it. That is the ABSENCE of a policy, not a bypass
/// of one; a mesh that installs RLS is gated everywhere.</para>
/// </summary>
internal static class StaticContentGate
{
    /// <summary>The gate's verdict for one <c>/static</c> request.</summary>
    internal enum Verdict
    {
        /// <summary>Serve the file.</summary>
        Allowed,

        /// <summary>Anonymous caller, and anonymous access does not suffice → 401.</summary>
        NotAuthenticated,

        /// <summary>Authenticated caller without Read on the owning node → 403.</summary>
        Forbidden,
    }

    /// <summary>
    /// Ceiling for the caller's POSITIVE Read grant to surface on the live permission stream.
    /// The wait must be positive-shaped: the fold's first emission can be the premature empty
    /// seed, so a "wait for any emission" would deny a legitimately-entitled caller. A caller with
    /// no grant pays the full wait and is then denied. Same rationale and window as
    /// <c>CourseAssetEndpoints.GrantWait</c>, the sibling gated-asset endpoint.
    /// </summary>
    internal static readonly TimeSpan GrantWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Rejects a <c>/static</c> path whose segments cannot be safely attributed to an owner.
    /// Empty segments (<c>//</c>) and dot segments (<c>.</c> / <c>..</c>) are path traversal —
    /// <c>FileSystemStreamProvider</c> does a bare <c>Path.Combine</c>, so
    /// <c>content/PublicSpace/../PrivateSpace/secret.pdf</c> would read another partition's file
    /// while this gate attributed it to <c>PublicSpace</c>. A double quote would break out of the
    /// quoting <see cref="IPathResolver"/> puts around each path segment when it builds its
    /// <c>path:</c> query. Pure — the rule is pinned without touching a file system.
    /// </summary>
    /// <param name="path">A decoded <c>/static</c> path or path fragment.</param>
    /// <returns><c>true</c> when every segment is safe to attribute and serve.</returns>
    internal static bool IsSafePath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == ".." || segment.Contains('"'))
                return false;
        }
        return true;
    }

    /// <summary>
    /// The owner-path CANDIDATE for <c>/static/{collection}/{filePath}</c> served from a
    /// MESH-LEVEL collection — the raw backing store (<c>storage</c>) that every per-node
    /// collection is mounted against.
    ///
    /// <para>The store has no owner of its own: its address is the mesh root, where nobody holds
    /// Read, so attributing files to it would deny every icon and thumbnail in the portal. It does
    /// however have a known layout, because the per-node mounts are what create it —
    /// <c>MemexConfiguration</c> mounts each Space's writable <c>content</c> collection at
    /// <c>{store}/content/{nodePath}</c> and <c>MapContentCollection("attachments", "storage",
    /// $"attachments/{nodePath}")</c> mounts attachments at <c>{store}/attachments/{nodePath}</c>.
    /// Every mount is <c>{mount}/{nodePath}/…</c>, so dropping the single mount segment leaves the
    /// owning node path followed by the file's path within it. That inverse is exactly what
    /// <c>MeshNodeImageHelper</c>, <c>MarkdownFileParser</c>, <c>MeshNodeThumbnailControl</c> and
    /// <c>BrandingResolver</c> assume when they BUILD <c>/static/storage/content/{nodePath}/{file}</c>
    /// URLs.</para>
    ///
    /// <para>A path with no mount segment (a file at the store root) has no owner and yields
    /// <c>null</c> → denied.</para>
    /// </summary>
    /// <param name="filePath">The decoded path within the collection (e.g. <c>content/ACME/logo.svg</c>).</param>
    /// <returns>The candidate owner path, or <c>null</c> when the file cannot be attributed.</returns>
    internal static string? RootStoreOwnerCandidate(string? filePath)
    {
        if (!IsSafePath(filePath))
            return null;
        var trimmed = filePath!.Trim('/');
        var slash = trimmed.IndexOf('/');
        if (slash <= 0)
            return null;
        var rest = trimmed[(slash + 1)..];
        return string.IsNullOrEmpty(rest) ? null : rest;
    }

    /// <summary>
    /// The owner-path CANDIDATE for <c>/static/{address}/{collection}/{filePath}</c>. The
    /// collection is mounted ON <paramref name="addressPrefix"/>, so that node is the owner floor;
    /// appending the file path lets <see cref="IPathResolver"/> attribute the file to a DEEPER node
    /// when the content mirrors the node tree (see the type remarks).
    /// </summary>
    /// <param name="addressPrefix">The resolved node path the collection is mounted on.</param>
    /// <param name="filePath">The decoded path within the collection.</param>
    /// <returns>The candidate owner path, or <c>null</c> when either part is unsafe.</returns>
    internal static string? AddressOwnerCandidate(string? addressPrefix, string? filePath)
    {
        if (string.IsNullOrEmpty(addressPrefix) || !IsSafePath(addressPrefix))
            return null;
        if (!IsSafePath(filePath))
            return null;
        return $"{addressPrefix.Trim('/')}/{filePath!.Trim('/')}";
    }

    /// <summary>
    /// Resolves <paramref name="ownerCandidate"/> to its owning mesh node and answers whether
    /// <paramref name="caller"/> may read it. See the type remarks for why this is the same
    /// decision the ordinary content read makes.
    /// </summary>
    /// <param name="hub">The hub to evaluate on — any hub works, the evaluator is a pure function over the process-wide node-stream cache.</param>
    /// <param name="caller">The request's resolved identity; never null, anonymous when unauthenticated.</param>
    /// <param name="ownerCandidate">The candidate owner path from one of the two <c>*OwnerCandidate</c> helpers.</param>
    /// <param name="logger">Optional logger for denials and infrastructure faults.</param>
    /// <returns>A single-emission observable carrying the verdict.</returns>
    internal static IObservable<Verdict> Authorize(
        IMessageHub hub,
        AccessContext? caller,
        string? ownerCandidate,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(hub);

        // No RLS installed ⇒ no policy to enforce. The canonical read path does exactly this:
        // AccessControlPipeline invokes next() without a check when the delegate is absent.
        if (hub.Configuration.Get<EffectivePermissionsDelegate>() is null)
            return Observable.Return(Verdict.Allowed);

        var userId = caller?.ObjectId;
        var isAuthenticated = !string.IsNullOrEmpty(userId)
                              && caller?.IsVirtual != true
                              && !string.Equals(userId, WellKnownUsers.Anonymous, StringComparison.Ordinal);
        if (!isAuthenticated)
            userId = WellKnownUsers.Anonymous;
        var denied = isAuthenticated ? Verdict.Forbidden : Verdict.NotAuthenticated;

        if (string.IsNullOrEmpty(ownerCandidate))
        {
            logger?.LogInformation(
                "StaticContentGate: request denied — the file cannot be attributed to an owning node (caller={Caller})",
                userId);
            return Observable.Return(denied);
        }

        var resolver = hub.ServiceProvider.GetService<IPathResolver>();
        if (resolver is null)
        {
            logger?.LogWarning(
                "StaticContentGate: no IPathResolver registered — cannot attribute '{Candidate}'; denying",
                ownerCandidate);
            return Observable.Return(denied);
        }

        var accessService = hub.ServiceProvider.GetService<AccessService>();

        return resolver.ResolvePath(ownerCandidate)
            .Take(1)
            .SelectMany(resolution => string.IsNullOrEmpty(resolution?.Prefix)
                ? Observable.Return(denied)
                // Observable.Using re-establishes the CALLER's identity for the whole cold
                // evaluation: the resolver hops schedulers, and AccessContext is an AsyncLocal
                // that does not flow across them — without this the evaluator's capability
                // checks (IsApiToken, claim roles) would read a null/foreign ambient context.
                // The SUBJECT is passed explicitly as userId, so the fold itself never depends
                // on the ambient value.
                : Observable.Using(
                        () => accessService?.SwitchAccessContext(caller)
                              ?? System.Reactive.Disposables.Disposable.Empty,
                        _ => hub.GetEffectivePermissions(resolution!.Prefix, userId!))
                    // Positive-shaped wait — see GrantWait.
                    .Where(p => p.HasFlag(Permission.Read))
                    .Take(1)
                    .Select(_ => Verdict.Allowed))
            .Timeout(GrantWait)
            .Catch((Exception ex) =>
            {
                if (ex is TimeoutException)
                    logger?.LogInformation(
                        "StaticContentGate: no Read grant for {Caller} on '{Candidate}' within {Wait} — denying",
                        userId, ownerCandidate, GrantWait);
                else
                    logger?.LogWarning(ex,
                        "StaticContentGate: authorization of '{Candidate}' for {Caller} failed — failing closed",
                        ownerCandidate, userId);
                return Observable.Empty<Verdict>();
            })
            .DefaultIfEmpty(denied);
    }

    /// <summary>
    /// The HTTP result for a denial: 401 for an anonymous caller (sign in and retry), 403 for an
    /// authenticated one (signing in again will not help). Mirrors the sibling gated-asset
    /// endpoint <c>CourseAssetEndpoints</c>.
    /// </summary>
    /// <param name="verdict">A non-<see cref="Verdict.Allowed"/> verdict.</param>
    /// <returns>The 401/403 result.</returns>
    internal static IResult DenyResult(Verdict verdict) =>
        verdict == Verdict.NotAuthenticated
            ? Results.Unauthorized()
            : Results.StatusCode(StatusCodes.Status403Forbidden);
}
