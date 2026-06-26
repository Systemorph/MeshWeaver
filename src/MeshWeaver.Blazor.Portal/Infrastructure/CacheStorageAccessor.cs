using Microsoft.FluentUI.AspNetCore.Components.Utilities;
using Microsoft.JSInterop;

namespace MeshWeaver.Blazor.Portal.Infrastructure;

/// <summary>
/// Accessor over the browser Cache Storage API (via JS interop) used to cache HTTP responses,
/// invalidating the cache automatically when the application version changes.
/// </summary>
/// <param name="js">The JS runtime used to invoke the cache-storage interop module.</param>
/// <param name="vs">The version service used to detect application upgrades and bust the cache.</param>
public class CacheStorageAccessor(IJSRuntime js, IAppVersionService vs) : JSModule(js, "./_content/MeshWeaver.Blazor.Portal/Layout/PortalLayoutBase.razor.js")
{
    private string? currentCacheVersion;

    /// <summary>
    /// Stores a request/response pair in the browser cache.
    /// </summary>
    /// <param name="requestMessage">The request used as the cache key.</param>
    /// <param name="responseMessage">The response whose body is cached.</param>
    /// <returns>A task that completes once the entry is written.</returns>
    public async ValueTask PutAsync(HttpRequestMessage requestMessage, HttpResponseMessage responseMessage)
    {
        var requestMethod = requestMessage.Method.Method;
        var requestBody = await GetRequestBodyAsync(requestMessage);
        var responseBody = await responseMessage.Content.ReadAsStringAsync();

        await InvokeVoidAsync("put", requestMessage.RequestUri!, requestMethod, requestBody, responseBody);
    }

    /// <summary>
    /// Stores a request/response pair in the browser cache and returns the cached response body.
    /// </summary>
    /// <param name="requestMessage">The request used as the cache key.</param>
    /// <param name="responseMessage">The response whose body is cached and returned.</param>
    /// <returns>The response body that was cached.</returns>
    public async ValueTask<string> PutAndGetAsync(HttpRequestMessage requestMessage, HttpResponseMessage responseMessage)
    {
        var requestMethod = requestMessage.Method.Method;
        var requestBody = await GetRequestBodyAsync(requestMessage);
        var responseBody = await responseMessage.Content.ReadAsStringAsync();

        await InvokeVoidAsync("put", requestMessage.RequestUri!, requestMethod, requestBody, responseBody);

        return responseBody;
    }

    /// <summary>
    /// Retrieves a cached response body for the given request, initializing (and version-validating) the cache on first use.
    /// </summary>
    /// <param name="requestMessage">The request used as the cache key.</param>
    /// <returns>The cached response body, or an empty string when not cached.</returns>
    public async ValueTask<string> GetAsync(HttpRequestMessage requestMessage)
    {
        if (currentCacheVersion is null)
        {
            await InitializeCacheAsync();
        }

        var result = await InternalGetAsync(requestMessage);

        return result;
    }
    private async ValueTask<string> InternalGetAsync(HttpRequestMessage requestMessage)
    {
        var requestMethod = requestMessage.Method.Method;
        var requestBody = await GetRequestBodyAsync(requestMessage);
        var result = await InvokeAsync<string>("get", requestMessage.RequestUri!, requestMethod, requestBody);

        return result;
    }

    /// <summary>
    /// Removes the cached entry for the given request.
    /// </summary>
    /// <param name="requestMessage">The request whose cached entry is removed.</param>
    /// <returns>A task that completes once the entry is removed.</returns>
    public async ValueTask RemoveAsync(HttpRequestMessage requestMessage)
    {
        var requestMethod = requestMessage.Method.Method;
        var requestBody = await GetRequestBodyAsync(requestMessage);

        await InvokeVoidAsync("remove", requestMessage.RequestUri!, requestMethod, requestBody);
    }

    /// <summary>
    /// Clears all entries from the browser cache.
    /// </summary>
    /// <returns>A task that completes once the cache is cleared.</returns>
    public async ValueTask RemoveAllAsync()
    {
        await InvokeVoidAsync("removeAll");
    }
    private static async ValueTask<string> GetRequestBodyAsync(HttpRequestMessage requestMessage)
    {
        var requestBody = string.Empty;
        if (requestMessage.Content is not null)
        {
            requestBody = await requestMessage.Content.ReadAsStringAsync();
        }

        return requestBody;
    }

    private async Task InitializeCacheAsync()
    {
        // last version cached is stored in appVersion
        var requestMessage = new HttpRequestMessage(HttpMethod.Get, "appVersion");

        // get the last version cached
        var result = await InternalGetAsync(requestMessage);
        if (!result.Equals(vs.Version))
        {
            // running newer version now, clear cache, and update version in cache
            await RemoveAllAsync();
            var requestBody = await GetRequestBodyAsync(requestMessage);
            await InvokeVoidAsync(
                "put",
                requestMessage.RequestUri!,
                requestMessage.Method.Method,
                requestBody,
                vs.Version);
        }
        //
        currentCacheVersion = vs.Version;
    }
}
