using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.AI.Persistence;
using MeshWeaver.Layout;
using Microsoft.Extensions.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// 🚨 A JSON OBJECT SENT FOR A <c>string</c> TOOL PARAMETER MUST NOT KILL THE AGENT ROUND (issue #1419).
///
/// <para>The mesh tool surface carries JSON documents in <c>string</c> parameters and the model-facing
/// <c>[Description]</c>s say exactly that — <c>MeshPlugin.Create</c>'s is <i>"JSON MeshNode with
/// required: id, name, nodeType, namespace. Example: {…}"</i>, <c>MeshPlugin.Patch</c>'s is <i>"JSON
/// object with ONLY the fields to change"</i>, <c>Update</c>/<c>Delete</c> ask for a <i>JSON array</i>.
/// A model that sends the object (or array) literal is doing what it was told; the binder is what has
/// no rule for it. <c>ReflectionAIFunction</c>'s per-parameter marshaller runs
/// <c>JsonSerializer.Deserialize(element, stringTypeInfo)</c>, <c>StringConverter</c> refuses the
/// <c>StartObject</c> token, and the <c>JsonException</c> escapes
/// <c>AccessContextAIFunction.InvokeCoreAsync</c> — the tool body never runs and the whole round fails
/// (the production trace: <c>[ThreadExec] ERROR … Cannot get the value of a token type 'StartObject'
/// as a string</c>, in a story-writing thread).</para>
///
/// <para>These drive REAL <c>AIFunction</c>s built by the real <c>AIFunctionFactory</c> and invoked
/// through the real wrapper, with the argument supplied as a raw <see cref="JsonElement"/> — i.e. the
/// shape the model's tool call actually arrives in. Nothing is mocked.</para>
/// </summary>
public class ToolArgumentJsonObjectTest
{
    // ── The reported shape, and its siblings ─────────────────────────────

    /// <summary>The exact incident shape: an object where a <c>string</c> is declared.</summary>
    [Fact(Timeout = 10_000)]
    public async Task JsonObject_ForAStringParameter_ArrivesAsItsRawJsonText()
    {
        var result = await Invoke(CreateNode, ("node", """{"id":"A","namespace":"MyOrg"}"""));

        result.Should().Be("""created:{"id":"A","namespace":"MyOrg"}""",
            "the parameter's value IS the JSON document — the tool body parses it itself, against "
            + "the hub's JsonSerializerOptions where the $type discriminators are registered");
    }

    /// <summary>The sibling shape: <c>Update</c>/<c>Delete</c> declare <i>JSON array</i> in a string.</summary>
    [Fact(Timeout = 10_000)]
    public async Task JsonArray_ForAStringParameter_ArrivesAsItsRawJsonText()
    {
        var result = await Invoke(CreateNode, ("node", """["A","B"]"""));

        result.Should().Be("""created:["A","B"]""");
    }

    /// <summary>A nested object — the <c>patch</c> shape (<c>{"content":{…}}</c>) — round-trips whole.</summary>
    [Fact(Timeout = 10_000)]
    public async Task NestedJsonObject_KeepsItsFullStructure()
    {
        const string json = """{"content":{"logo":"<svg/>","tags":[1,2]},"name":"N"}""";

        (await Invoke(CreateNode, ("node", json))).Should().Be($"created:{json}");
    }

    /// <summary>Only the <c>string</c> parameters are touched; the rest bind exactly as before.</summary>
    [Fact(Timeout = 10_000)]
    public async Task MixedArguments_OnlyTheStringParameterIsNormalized()
    {
        var result = await Invoke(PatchNode,
            ("path", "\"@User/rbuergi/my-node\""),   // a JSON *string* — already binds, untouched
            ("fields", """{"name":"New Name"}"""),   // an object for a string param — normalized
            ("replaceAll", "true"));                 // a bool for a bool param — untouched

        result.Should().Be("""patched:@User/rbuergi/my-node|{"name":"New Name"}|True""");
    }

    /// <summary>A model that sends an object where a POCO is declared still binds through the SDK.</summary>
    [Fact(Timeout = 10_000)]
    public async Task JsonObject_ForANonStringParameter_StillBindsNormally()
    {
        var result = await Invoke(TypedTool, ("filter", """{"term":"laptop","take":3}"""));

        result.Should().Be("typed:laptop/3",
            "the normalization is scoped to parameters the method declares as string — an object "
            + "destined for a POCO parameter must keep binding through the SDK's own marshaller");
    }

    // ── The bound: what must still fail loudly ───────────────────────────

    /// <summary>
    /// A NUMBER for a <c>string</c> parameter is a genuine model error with no defensible
    /// reinterpretation — it must keep throwing. The fix is scoped to object/array, the two shapes the
    /// tool descriptions actively ask for; silently stringifying scalars would mask prompt bugs.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task JsonNumber_ForAStringParameter_StillThrows()
    {
        var act = () => Invoke(CreateNode, ("node", "123"));

        await act.Should().ThrowAsync<JsonException>();
    }

    /// <summary>A plain JSON string is unaffected — it already bound, and must still bind verbatim.</summary>
    [Fact(Timeout = 10_000)]
    public async Task JsonString_ForAStringParameter_IsUnchanged()
    {
        (await Invoke(CreateNode, ("node", "\"already text\""))).Should().Be("created:already text");
    }

    /// <summary>
    /// The as-written DOM shape: some call paths hand the binder a <see cref="JsonNode"/> rather than
    /// a <see cref="JsonElement"/>. Same value, same answer — the rule is about the JSON kind, not
    /// about which DOM type happens to carry it.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task JsonNodeObject_IsNormalizedLikeAJsonElement()
    {
        var wrapper = Wrap(AIFunctionFactory.Create(CreateNode));

        var result = await wrapper.InvokeAsync(
            new AIFunctionArguments { ["node"] = JsonNode.Parse("""{"id":"A"}""") },
            CancellationToken.None);

        result?.ToString().Should().Be("""created:{"id":"A"}""");
    }

    /// <summary>Re-invoking with an already-normalized value is a no-op, not a double-encode.</summary>
    [Fact(Timeout = 10_000)]
    public async Task AlreadyNormalizedValue_IsIdempotent()
    {
        var wrapper = Wrap(AIFunctionFactory.Create(CreateNode));
        var arguments = new AIFunctionArguments
        {
            ["node"] = JsonDocument.Parse("""{"id":"A"}""").RootElement
        };

        var first = await wrapper.InvokeAsync(arguments, CancellationToken.None);
        var second = await wrapper.InvokeAsync(arguments, CancellationToken.None);

        second?.ToString().Should().Be(first?.ToString(),
            "a coerced value is a string and no longer matches the rule — re-marshalling the same "
            + "arguments must not re-encode it into an escaped JSON literal");
    }

    // ── Harness ──────────────────────────────────────────────────────────

    private static async Task<string?> Invoke(Delegate tool, params (string Name, string Json)[] arguments)
    {
        var wrapper = Wrap(AIFunctionFactory.Create(tool));
        var args = new AIFunctionArguments();
        foreach (var (name, json) in arguments)
            args[name] = JsonDocument.Parse(json).RootElement;

        var result = await wrapper.InvokeAsync(args, CancellationToken.None);
        return result?.ToString();
    }

    private static AccessContextAIFunction Wrap(AIFunction inner) =>
        // accessService is only touched when the chat carries a user context; the stub carries none.
        new(inner, new TestAgentChat(), accessService: null!);

    // ── Tool methods (the real shapes: a JSON document carried in a string) ──

    [Description("Test tool mirroring MeshPlugin.Create — the node is a JSON document, as text.")]
    private static string CreateNode(
        [Description("JSON MeshNode. Example: {\"id\":\"my-page\",\"nodeType\":\"Markdown\"}")] string node)
        => $"created:{node}";

    [Description("Test tool mirroring MeshPlugin.Patch/EditContent — mixed parameter types.")]
    private static string PatchNode(
        [Description("Path to the node")] string path,
        [Description("JSON object with ONLY the fields to change")] string fields,
        [Description("Replace every occurrence")] bool replaceAll)
        => $"patched:{path}|{fields}|{replaceAll}";

    [Description("Test tool taking a structured parameter — must keep binding through the SDK.")]
    private static string TypedTool(SearchFilter filter) => $"typed:{filter.Term}/{filter.Take}";

    /// <summary>A structured tool parameter — deliberately NOT a string.</summary>
    public sealed record SearchFilter(string Term, int Take);

    /// <summary>
    /// Minimal IAgentChat stub — AccessContextAIFunction only reads
    /// <see cref="IAgentChat.ExecutionContext"/>; the rest get the interface's default no-ops.
    /// </summary>
    private sealed class TestAgentChat : IAgentChat
    {
        public void SetContext(AgentContext? applicationContext) { }
        public void SetSelectedAgent(string? agentName) { }
        public Task ResumeAsync(ChatConversation conversation) => Task.CompletedTask;
        public Task<IReadOnlyList<AgentDisplayInfo>> GetOrderedAgentsAsync() =>
            Task.FromResult<IReadOnlyList<AgentDisplayInfo>>([]);
        public async IAsyncEnumerable<ChatMessage> GetResponseAsync(
            IReadOnlyCollection<ChatMessage> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; yield break; }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IReadOnlyCollection<ChatMessage> messages,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; yield break; }
        public void SetThreadId(string threadId) { }
        public void DisplayLayoutArea(LayoutAreaControl layoutAreaControl) { }
        public AgentContext? Context => null;
    }
}
