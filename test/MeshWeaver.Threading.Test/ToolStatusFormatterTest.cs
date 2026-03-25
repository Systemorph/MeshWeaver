using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using MeshWeaver.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace MeshWeaver.Threading.Test;

public class ToolStatusFormatterTest
{
    [Fact]
    public void Get_FormatsWithPath()
    {
        var call = MakeCall("Get", new() { ["path"] = "@org/Acme" });
        ToolStatusFormatter.Format(call).Should().Be("Fetching @org/Acme");
    }

    [Fact]
    public void Search_FormatsWithQuery()
    {
        var call = MakeCall("Search", new() { ["query"] = "nodeType:Agent" });
        ToolStatusFormatter.Format(call).Should().Be("Searching \"nodeType:Agent\"");
    }

    [Fact]
    public void Create_ShowsGenericMessage()
    {
        var call = MakeCall("Create", new() { ["node"] = "{}" });
        ToolStatusFormatter.Format(call).Should().Be("Creating node...");
    }

    [Fact]
    public void NavigateTo_FormatsWithPath()
    {
        var call = MakeCall("NavigateTo", new() { ["path"] = "@Doc/Architecture" });
        ToolStatusFormatter.Format(call).Should().Be("Navigating to @Doc/Architecture");
    }

    [Fact]
    public void SearchWeb_FormatsWithQuery()
    {
        var call = MakeCall("SearchWeb", new() { ["query"] = "MeshWeaver docs" });
        ToolStatusFormatter.Format(call).Should().Be("Searching web for \"MeshWeaver docs\"");
    }

    [Fact]
    public void FetchWebPage_FormatsWithUrl()
    {
        var call = MakeCall("FetchWebPage", new() { ["url"] = "https://example.com" });
        ToolStatusFormatter.Format(call).Should().Be("Fetching https://example.com");
    }

    [Fact]
    public void DelegateToAgent_FormatsWithAgentName()
    {
        var call = MakeCall("delegate_to_agent", new() { ["agentName"] = "Researcher", ["task"] = "find info" });
        ToolStatusFormatter.Format(call).Should().Be("Delegating to Researcher...");
    }

    [Fact]
    public void DelegateToSpecific_ExtractsNameFromFunctionName()
    {
        var call = MakeCall("delegate_to_Orchestrator", null);
        ToolStatusFormatter.Format(call).Should().Be("Delegating to Orchestrator...");
    }

    [Fact]
    public void HandoffToAgent_FormatsWithAgentName()
    {
        var call = MakeCall("handoff_to_agent", new() { ["agentName"] = "Worker" });
        ToolStatusFormatter.Format(call).Should().Be("Handing off to Worker...");
    }

    [Fact]
    public void StorePlan_ShowsGenericMessage()
    {
        var call = MakeCall("store_plan", new() { ["planContent"] = "## Plan" });
        ToolStatusFormatter.Format(call).Should().Be("Storing plan...");
    }

    [Fact]
    public void Unknown_FallsBack()
    {
        var call = MakeCall("custom_tool", null);
        ToolStatusFormatter.Format(call).Should().Be("Calling custom_tool...");
    }

    [Fact]
    public void TruncatesLongValues()
    {
        var longPath = new string('x', 100);
        var call = MakeCall("Get", new() { ["path"] = longPath });
        var result = ToolStatusFormatter.Format(call);
        result.Should().Contain("...");
        result.Length.Should().BeLessThan(80);
    }

    [Fact]
    public void HandlesJsonElementArgs()
    {
        // Simulate args coming as JsonElement (common from AI framework)
        var json = JsonSerializer.Deserialize<JsonElement>("{\"path\": \"@org/test\"}");
        var args = new Dictionary<string, object?>
        {
            ["path"] = json.GetProperty("path")
        };
        var call = MakeCall("Get", args);
        ToolStatusFormatter.Format(call).Should().Be("Fetching @org/test");
    }

    [Fact]
    public void HandlesMissingArgs()
    {
        var call = MakeCall("Get", null);
        ToolStatusFormatter.Format(call).Should().Be("Fetching ...");
    }

    [Fact]
    public void AddComment_FormatsWithSelectedText()
    {
        var call = MakeCall("AddComment", new() { ["selectedText"] = "quarterly results" });
        ToolStatusFormatter.Format(call).Should().Be("Adding comment on \"quarterly results\"...");
    }

    // --- Reference link conversion tests ---

    [Fact]
    public void ConvertReferences_InlinePathBecomesLink()
    {
        var method = typeof(MeshWeaver.AI.ThreadMessageLayoutAreas)
            .GetMethod("ConvertReferencesToLinks", BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull();

        var result = (string)method!.Invoke(null, ["Check out @User/rbuergi/agents-comparison for details"])!;
        // Should produce markdown link with @prefix in href for LinkUrlCleanupExtension to resolve
        result.Should().Contain("[`@User/rbuergi/agents-comparison`](@User/rbuergi/agents-comparison)");
    }

    [Fact]
    public void ConvertReferences_AlreadyLinkedSkipped()
    {
        var method = typeof(MeshWeaver.AI.ThreadMessageLayoutAreas)
            .GetMethod("ConvertReferencesToLinks", BindingFlags.NonPublic | BindingFlags.Static);

        var result = (string)method!.Invoke(null, ["Already linked [@Doc/Foo](/Doc/Foo) here"])!;
        result.Should().NotContain("[[");
    }

    [Fact]
    public void ConvertReferences_AbsolutePathPreserved()
    {
        var method = typeof(MeshWeaver.AI.ThreadMessageLayoutAreas)
            .GetMethod("ConvertReferencesToLinks", BindingFlags.NonPublic | BindingFlags.Static);

        // @/path is absolute — LinkUrlCleanupExtension will strip @ and see /path = absolute
        var result = (string)method!.Invoke(null, ["See @/User/rbuergi/doc for info"])!;
        result.Should().Contain("@/User/rbuergi/doc");
    }

    private static FunctionCallContent MakeCall(string name, Dictionary<string, object?>? args)
    {
        return new FunctionCallContent(Guid.NewGuid().ToString("N"), name, args);
    }
}
