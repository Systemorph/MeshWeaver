using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeshWeaver.AI.OpenAI;

/// <summary>
/// Pipeline policy that opts every OpenRouter chat-completion request into prompt caching by
/// injecting OpenRouter's top-level <c>"cache_control": {"type": "ephemeral"}</c> field into the
/// outgoing request body.
///
/// <para>Providers with automatic caching (OpenAI, DeepSeek, Grok, Groq, Gemini 2.5, …) ignore the
/// field, but providers that require EXPLICIT cache breakpoints (Anthropic, Qwen) get none without
/// it — so a multi-round agent turn on an <c>anthropic/*</c> model re-pays the full re-sent prefix
/// (tool schemas + system prompt + history) at 1× input price on every round. With the top-level
/// field OpenRouter places the breakpoint at the last cacheable block itself, which caches that
/// whole prefix incrementally: ~1.25× write on the per-round delta, ~0.1× read on everything
/// before it. Below the model's minimum cacheable size the provider simply doesn't cache (no
/// error), so injecting is always safe.</para>
///
/// <para>The resulting cache reads come back in the standard OpenAI usage shape
/// (<c>prompt_tokens_details.cached_tokens</c>), which the Microsoft.Extensions.AI adapter already
/// surfaces as <c>AdditionalCounts["InputTokenDetails.CachedTokenCount"]</c> — picked up unchanged
/// by <c>UsageTokens.SplitCache</c> and the whole token-accounting pipeline behind it.</para>
///
/// <para>The typed OpenAI SDK cannot express this OpenRouter extension field, hence the rewrite at
/// the transport pipeline — the sanctioned <see cref="PipelinePolicy"/> extension point. Attached
/// per-call (before the retry policy) by <see cref="OpenAIChatClientAgentFactory"/>, and ONLY for
/// endpoints where <see cref="AppliesTo"/> says the host is OpenRouter: other OpenAI-compatible
/// gateways may reject an unknown top-level field.</para>
/// </summary>
public sealed class OpenRouterPromptCachePolicy : PipelinePolicy
{
    /// <inheritdoc />
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    /// <inheritdoc />
    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Apply(message);
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
    }

    /// <summary>
    /// True when <paramref name="endpoint"/> targets OpenRouter (<c>openrouter.ai</c> or a
    /// subdomain) — the only gateway documented to accept the top-level <c>cache_control</c>
    /// extension. Null / relative / unparsable endpoints (including the SDK-default
    /// api.openai.com case) are not OpenRouter.
    /// </summary>
    public static bool AppliesTo(string? endpoint)
    {
        if (string.IsNullOrEmpty(endpoint) || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return false;
        return uri.Host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".openrouter.ai", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Rewrites the request body of chat-completion POSTs in place; leaves every other request
    /// (and any body <see cref="TryAddCacheControl"/> declines) untouched.
    /// </summary>
    private static void Apply(PipelineMessage message)
    {
        var request = message.Request;
        if (request.Content is null
            || !string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)
            || request.Uri is not { } uri
            || !uri.AbsolutePath.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return;

        using var buffer = new MemoryStream();
        request.Content.WriteTo(buffer, CancellationToken.None);
        var body = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        if (TryAddCacheControl(body) is { } rewritten)
            request.Content = BinaryContent.Create(BinaryData.FromString(rewritten));
    }

    /// <summary>
    /// Adds <c>"cache_control": {"type": "ephemeral"}</c> at the top level of a JSON request body.
    /// Returns the rewritten body, or null when no rewrite should happen: the body already carries
    /// a top-level <c>cache_control</c> (a caller's explicit choice wins), isn't a JSON object, or
    /// doesn't parse — never break a request we don't understand.
    /// </summary>
    internal static string? TryAddCacheControl(string requestBody)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(requestBody);
        }
        catch (JsonException)
        {
            return null;
        }
        if (root is not JsonObject obj || obj.ContainsKey("cache_control"))
            return null;
        obj["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
        return obj.ToJsonString();
    }
}
