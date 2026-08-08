using System.Text.Json;
using MeshWeaver.Mcp;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Issue #639 — a schema-mismatch call must NAME the bad argument.
///
/// <para>Tool schemas change between images (<c>create nodes</c>→<c>node</c>,
/// <c>delete path</c>→<c>paths</c>). Before this validation a caller on the older dialect got one of
/// two useless answers: the stale argument was <b>silently dropped</b> (the SDK leaves
/// <c>UnmappedMemberHandling</c> at <c>Skip</c>, so a renamed parameter with a default just RAN with
/// that default — the half-executed sequences of the 2026-07-24 plugin-install retrospective), or the
/// binder threw and <c>McpServerImpl</c> flattened it to the fixed text
/// <c>"An error occurred invoking 'create'."</c>, which names nothing.</para>
///
/// <para><b>These tests drive the REAL production tool declarations.</b> Each case builds the tool
/// through the very <c>McpServerTool.Create</c> path <c>WithToolsFromAssembly</c> uses in
/// <see cref="McpExtensions.AddMeshMcp"/>, so the schema under test IS the shipped schema — rename a
/// parameter on <see cref="McpMeshPlugin"/> and these expectations move with it.</para>
///
/// <para>The tool INSTANCE is never materialised: <c>McpServerTool.Create</c> stores the target
/// factory and only calls it at invoke time, so a schema-only test needs no mesh, no hub and no
/// session — it is a pure check of the contract the caller sees.</para>
///
/// <para>What is NOT covered here: the <c>CallToolFilter</c> wrapper itself. Short-circuiting it
/// end-to-end needs a <c>RequestContext&lt;CallToolRequestParams&gt;</c>, which requires a live
/// <c>McpServer</c> (transport + initialize handshake). The filter is a three-line adapter over
/// <see cref="McpArgumentValidation.Validate"/> — the decision logic all lives in the validated
/// method.</para>
/// </summary>
public class McpArgumentValidationTest
{
    /// <summary>
    /// The production tool declaration for a <see cref="McpMeshPlugin"/> method — same
    /// <c>McpServerTool.Create</c> path the server registration uses, so the input schema, the tool
    /// name (<c>EditContent</c> → <c>edit_content</c>) and the required-set are the shipped ones.
    /// </summary>
    private static Tool ToolFor(string methodName) =>
        McpServerTool.Create(
                typeof(McpMeshPlugin).GetMethod(methodName)!,
                _ => throw new InvalidOperationException("schema-only: the tool is never invoked"),
                new McpServerToolCreateOptions())
            .ProtocolTool;

    private static Dictionary<string, JsonElement> Args(params (string Name, string Json)[] arguments) =>
        arguments.ToDictionary(a => a.Name, a => JsonDocument.Parse(a.Json).RootElement);

    // ── The schemas the tests stand on (guards the validator against becoming a silent no-op) ──

    [Fact]
    public void RealToolSchemas_DeclareTheirParameters()
    {
        var create = ToolFor(nameof(McpMeshPlugin.Create));
        Assert.Equal("create", create.Name);
        var properties = create.InputSchema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("node", out _), "create's parameter is 'node' (singular)");
        Assert.False(properties.TryGetProperty("nodes", out _), "'nodes' is update's parameter, not create's");
    }

    // ── Unknown argument: name it, list the expected ones, and point at the likely rename ──

    [Fact]
    public void UnknownArgument_NamesIt_AndListsTheExpectedNames()
    {
        // The exact retrospective case: an older dialect calling `create` with `nodes`.
        var error = McpArgumentValidation.Validate(
            ToolFor(nameof(McpMeshPlugin.Create)),
            Args(("nodes", "\"[{\\\"id\\\":\\\"A\\\"}]\"")));

        Assert.NotNull(error);
        Assert.StartsWith("Error:", error);
        Assert.Contains("unknown argument 'nodes'", error);
        Assert.Contains("expected one of: node", error);
        Assert.Contains("Did you mean 'node'?", error);
        Assert.DoesNotContain("An error occurred", error);
    }

    [Fact]
    public void UnknownArgument_OnDelete_PointsAtTheRenamedPlural()
    {
        // The other half of the retrospective: `delete path` before it became `delete paths`.
        var error = McpArgumentValidation.Validate(
            ToolFor(nameof(McpMeshPlugin.Delete)),
            Args(("path", "\"ACME/Old\"")));

        Assert.NotNull(error);
        Assert.Contains("unknown argument 'path'", error);
        Assert.Contains("expected one of: paths", error);
        Assert.Contains("Did you mean 'paths'?", error);
    }

    [Fact]
    public void UnknownArgument_WithADefaultedTwin_IsRefused_NotSilentlyDropped()
    {
        // The dangerous shape: `replaceAll` has a default, so before this check the binder dropped
        // the misspelled `replace_all` and RAN the edit with replaceAll=false — a half-executed step
        // reported as success.
        var error = McpArgumentValidation.Validate(
            ToolFor(nameof(McpMeshPlugin.EditContent)),
            Args(
                ("path", "\"ACME/Doc\""),
                ("oldText", "\"a\""),
                ("newText", "\"b\""),
                ("replace_all", "true")));

        Assert.NotNull(error);
        Assert.Contains("unknown argument 'replace_all'", error);
        Assert.Contains("Did you mean 'replaceAll'?", error);
    }

    [Fact]
    public void UnknownArgument_WithNoCloseMatch_StillListsTheExpectedNames()
    {
        var error = McpArgumentValidation.Validate(
            ToolFor(nameof(McpMeshPlugin.Move)),
            Args(("frobnicate", "\"x\"")));

        Assert.NotNull(error);
        Assert.Contains("unknown argument 'frobnicate'", error);
        Assert.Contains("sourcePath", error);
        Assert.Contains("targetPath", error);
        Assert.DoesNotContain("Did you mean", error);
    }

    // ── Missing required argument ──────────────────────────────────────────────────────────────

    [Fact]
    public void MissingRequiredArgument_NamesIt()
    {
        var error = McpArgumentValidation.Validate(ToolFor(nameof(McpMeshPlugin.Create)), Args());

        Assert.NotNull(error);
        Assert.StartsWith("Error:", error);
        Assert.Contains("missing required argument 'node'", error);
        Assert.DoesNotContain("An error occurred", error);
    }

    [Fact]
    public void MissingOneOfSeveralRequired_NamesTheMissingOne()
    {
        var error = McpArgumentValidation.Validate(
            ToolFor(nameof(McpMeshPlugin.Move)),
            Args(("sourcePath", "\"OrgA/Child\"")));

        Assert.NotNull(error);
        Assert.Contains("missing required argument 'targetPath'", error);
    }

    // ── Wrong type ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WrongTypedArgument_NamesTheArgument_TheExpectedType_AndWhatArrived()
    {
        var error = McpArgumentValidation.Validate(
            ToolFor(nameof(McpMeshPlugin.Search)),
            Args(("query", "\"nodeType:Agent\""), ("limit", "\"twenty\"")));

        Assert.NotNull(error);
        Assert.Contains("argument 'limit'", error);
        Assert.Contains("expects integer", error);
        Assert.Contains("got string (\"twenty\")", error);
    }

    [Fact]
    public void ObjectSentForAStringArgument_IsNamed()
    {
        // `create` takes the node as a JSON *string*; sending the object itself throws inside the
        // binder today (JsonException → "An error occurred invoking 'create'.").
        var error = McpArgumentValidation.Validate(
            ToolFor(nameof(McpMeshPlugin.Create)),
            Args(("node", "{\"id\":\"A\"}")));

        Assert.NotNull(error);
        Assert.Contains("argument 'node'", error);
        Assert.Contains("expects string", error);
        Assert.Contains("got object", error);
    }

    // ── Calls that DO bind must pass untouched (the check may never reject a working call) ─────

    [Fact]
    public void WellFormedCall_Passes()
    {
        Assert.Null(McpArgumentValidation.Validate(
            ToolFor(nameof(McpMeshPlugin.Get)),
            Args(("path", "\"ACME/Project\""))));
    }

    [Fact]
    public void OmittedOptionalArguments_Pass()
    {
        // search(query, basePath = null, limit = 50) — only the required one supplied.
        Assert.Null(McpArgumentValidation.Validate(
            ToolFor(nameof(McpMeshPlugin.Search)),
            Args(("query", "\"nodeType:Agent\""))));
    }

    [Fact]
    public void NumericStringForAnIntegerArgument_Passes()
    {
        // McpJsonUtilities.DefaultOptions sets NumberHandling = AllowReadingFromString, so "20"
        // binds to int — the check must not be stricter than the binder.
        Assert.Null(McpArgumentValidation.Validate(
            ToolFor(nameof(McpMeshPlugin.Search)),
            Args(("query", "\"laptop\""), ("limit", "\"20\""))));
    }

    [Fact]
    public void NullForAnOptionalArgument_Passes()
    {
        // JSON null binds to null; the tool's own required-field answer beats a type complaint.
        Assert.Null(McpArgumentValidation.Validate(
            ToolFor(nameof(McpMeshPlugin.Search)),
            Args(("query", "\"laptop\""), ("basePath", "null"))));
    }

    [Fact]
    public void BooleanFlagSuppliedCorrectly_Passes()
    {
        Assert.Null(McpArgumentValidation.Validate(
            ToolFor(nameof(McpMeshPlugin.EditContent)),
            Args(
                ("path", "\"ACME/Doc\""),
                ("oldText", "\"a\""),
                ("newText", "\"b\""),
                ("replaceAll", "true"))));
    }

    // ── Why the validation has to exist: the binder's own behaviour, pinned ─────────────────────
    //
    // These drive Microsoft.Extensions.AI's binder — the component that owns the failure — under
    // McpJsonUtilities.DefaultOptions, the exact serializer options the MCP server builds its tools
    // with. They document what a caller got BEFORE the filter, and they fail loudly if an SDK bump
    // ever changes those semantics (at which point the filter's rationale needs re-reading).

    private static string SampleCreate(string node) => $"created:{node}";

    private static string SampleEditContent(string path, bool replaceAll = false) => $"{path}:{replaceAll}";

    private static AIFunction Bind(Delegate method) =>
        AIFunctionFactory.Create(method, new AIFunctionFactoryOptions { SerializerOptions = McpJsonUtilities.DefaultOptions });

    [Fact]
    public async Task Binder_SilentlyDropsAnUnknownArgument_AndRunsAnyway()
    {
        // The dangerous half of #639: `replace_all` is not a parameter, so the binder ignores it and
        // the call RUNS with replaceAll=false. No error, no warning — a half-executed step reported
        // as success. Nothing but a pre-binder check can catch this.
        var result = await Bind(SampleEditContent).InvokeAsync(new AIFunctionArguments
        {
            ["path"] = JsonDocument.Parse("\"ACME/Doc\"").RootElement,
            ["replace_all"] = JsonDocument.Parse("true").RootElement
        });

        Assert.Equal("ACME/Doc:False", result?.ToString());
    }

    [Fact]
    public async Task Binder_MissingRequiredArgument_ThrowsAMessageTheSdkThenDiscards()
    {
        // The binder DOES name the parameter — but McpServerImpl's tools/call wrapper turns any
        // non-McpException into the fixed "An error occurred invoking '<tool>'.", so this text never
        // reaches the caller. Our filter answers before the binder is ever asked.
        var error = await Assert.ThrowsAsync<ArgumentException>(async () =>
            await Bind(SampleCreate).InvokeAsync(new AIFunctionArguments
            {
                ["nodes"] = JsonDocument.Parse("\"[]\"").RootElement   // the old dialect's name
            }));

        Assert.Contains("node", error.Message);
    }

    [Fact]
    public async Task Binder_WrongTypedArgument_ThrowsAJsonExceptionNamingNoParameter()
    {
        // An object where a JSON string is expected: the failure is a raw JsonException about the
        // JSON shape — it does not name the argument at all, and the SDK discards it too.
        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await Bind(SampleCreate).InvokeAsync(new AIFunctionArguments
            {
                ["node"] = JsonDocument.Parse("{\"id\":\"A\"}").RootElement
            }));

        Assert.IsType<JsonException>(error);
    }
}
