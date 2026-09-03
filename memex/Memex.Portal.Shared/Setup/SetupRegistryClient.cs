using System.Collections.Immutable;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MeshWeaver.PluginCatalog;

namespace Memex.Portal.Shared.Setup;

/// <summary>
/// The two registry calls the first-run wizard makes — register this instance, then list what its
/// plan entitles it to — with NO mesh behind them.
///
/// <para>🚨 <b>Why not <c>InstanceRegistrationClient</c> / <c>RegistryPackageSource</c>:</b> both
/// take an <c>IMessageHub</c>, resolve an <c>IIoPool</c> from it, and (for registration) persist the
/// issued key as a mesh node. At the moment the wizard runs there is no hub, no pool and no store —
/// choosing the database is the question being asked. So these are plain HTTP, and the key is
/// persisted into the instance manifest instead, which is the pre-storage artifact for exactly this
/// kind of answer.</para>
///
/// <para><b>On <c>async</c>:</b> the repo bans it in hub-reachable and Blazor code, where it runs
/// continuations on the wrong scheduler and can park a turn-based actor. Neither exists here — this
/// is a plain ASP.NET request path in a host that deliberately composes no mesh, the same shape as
/// the <c>ExternalAuthController</c> callbacks. There is no Rx pipeline to resume inline and no
/// action block to block.</para>
/// </summary>
public sealed class SetupRegistryClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Registers this instance and returns the id, the issued key and the plan it was enrolled on.
    ///
    /// <para>🚨 <b>The key comes back exactly ONCE.</b> The registry never re-issues it, and the id
    /// cannot be re-registered after deletion, so a caller that does not persist the result has
    /// permanently burnt that id.</para>
    /// </summary>
    /// <param name="registryUrl">The registry base URL, e.g. <c>https://memex.meshweaver.cloud</c>.</param>
    /// <param name="instanceId">The id to claim — a guid the instance minted for itself.</param>
    /// <param name="displayName">The human-readable name the operator typed.</param>
    /// <param name="bootstrapKey">A registration key, when the operator has one. EMPTY is the
    /// ordinary case: an un-keyed registration is an OPEN one, which the registry enrols into its
    /// default plan (the free tier).</param>
    /// <param name="homeUrl">Where this instance will be reachable, for the registry's records.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The registration outcome — never null on success.</returns>
    /// <exception cref="SetupRegistryException">The registry refused or could not be reached, with
    /// a message written for the operator reading it on the setup page.</exception>
    public async Task<InstanceRegistrationPayloads.Response> RegisterAsync(
        string registryUrl, string instanceId, string displayName,
        string? bootstrapKey = null, string? homeUrl = null,
        CancellationToken cancellationToken = default)
    {
        var url = Combine(registryUrl, InstanceRegistrationPayloads.Route);
        var request = new InstanceRegistrationPayloads.Request(
            BootstrapKey: (bootstrapKey ?? "").Trim(),
            InstanceId: instanceId,
            DisplayName: displayName ?? "",
            Description: "",
            HomeUrl: homeUrl ?? "");

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(url, request, Json, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // The operator can fix a wrong URL or an offline registry; say which one it is rather
            // than surfacing a transport exception on a setup form.
            throw new SetupRegistryException(
                $"Could not reach the registry at {registryUrl}. Check the address and that this "
                + $"machine can reach it ({ex.GetType().Name}).", ex);
        }

        if (!response.IsSuccessStatusCode)
            throw new SetupRegistryException(await DescribeFailureAsync(response, registryUrl, cancellationToken));

        var result = await response.Content.ReadFromJsonAsync<InstanceRegistrationPayloads.Response>(
            Json, cancellationToken);
        if (result is null || string.IsNullOrWhiteSpace(result.InstanceKey))
            // A 200 with no key is not a success: the instance would believe it had registered and
            // then be unable to fetch anything, with the id already claimed.
            throw new SetupRegistryException(
                $"The registry at {registryUrl} accepted the registration but returned no instance "
                + "key. The id may now be claimed; choose a different one or contact the registry's "
                + "administrator.");
        return result;
    }

    /// <summary>
    /// Lists the packages this instance is entitled to, using the key it was just issued.
    ///
    /// <para>An empty list is a legitimate answer — a plan may grant nothing — and is NOT treated as
    /// a failure: the wizard says so and offers what the image itself ships.</para>
    /// </summary>
    /// <param name="registryUrl">The registry base URL.</param>
    /// <param name="instanceKey">The <c>mwi_</c> key, in the clear.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task<ImmutableList<PackageManifest>> ListPackagesAsync(
        string registryUrl, string? instanceKey, CancellationToken cancellationToken = default)
    {
        var url = Combine(registryUrl, RegistryPackageSource.RoutePrefix);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // Per-REQUEST, never on the client: the same HttpClient may serve another registry, and a
        // default header would leak this instance's key to it.
        if (!string.IsNullOrWhiteSpace(instanceKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", instanceKey.Trim());

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new SetupRegistryException(
                $"Could not reach the registry at {registryUrl} to list packages "
                + $"({ex.GetType().Name}).", ex);
        }

        if (!response.IsSuccessStatusCode)
            throw new SetupRegistryException(await DescribeFailureAsync(response, registryUrl, cancellationToken));

        var listing = await response.Content.ReadFromJsonAsync<ListResponse>(Json, cancellationToken);
        return [.. listing?.Packages ?? []];
    }

    /// <summary>The listing shape, mirroring <c>RegistryPackageSource</c>'s private one.</summary>
    private sealed record ListResponse(IReadOnlyList<PackageManifest>? Packages);

    /// <summary>
    /// A refusal in the operator's terms. The status alone is not actionable on a setup form, and
    /// the three that actually happen each have a different fix.
    /// </summary>
    private static async Task<string> DescribeFailureAsync(
        HttpResponseMessage response, string registryUrl, CancellationToken cancellationToken)
    {
        var body = "";
        try { body = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim(); }
        catch { /* the status is the message when the body cannot be read */ }
        if (body.Length > 300) body = body[..300] + "…";

        var reason = (int)response.StatusCode switch
        {
            401 or 403 =>
                "the registry refused the key. An open registration needs no key at all — leave it "
                + "blank — and a key that was minted for a plan may have been revoked or already used.",
            409 =>
                "that instance id is already claimed. Ids are global and are never re-issued, even "
                + "after a deletion; generate a new one.",
            404 =>
                $"there is no registry at {registryUrl} — the address answers, but not with a "
                + "registry API. Check the URL.",
            _ => $"the registry answered {(int)response.StatusCode} {response.ReasonPhrase}.",
        };
        return string.IsNullOrEmpty(body) ? reason : $"{reason} ({body})";
    }

    private static string Combine(string baseUrl, string route) =>
        (baseUrl ?? "").TrimEnd('/') + route;
}

/// <summary>
/// A registry call the operator can act on — the message is written for the setup page, not for a
/// log.
/// </summary>
public sealed class SetupRegistryException : Exception
{
    /// <summary>Creates the exception with an operator-facing message.</summary>
    /// <param name="message">What went wrong and what to do about it.</param>
    public SetupRegistryException(string message) : base(message) { }

    /// <summary>Creates the exception with an operator-facing message and the underlying cause.</summary>
    /// <param name="message">What went wrong and what to do about it.</param>
    /// <param name="inner">The transport or serialization failure beneath it.</param>
    public SetupRegistryException(string message, Exception inner) : base(message, inner) { }
}
