using System;
using System.Collections.Generic;
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
        var call = MakeCall("delegate_to_Navigator", null);
        ToolStatusFormatter.Format(call).Should().Be("Delegating to Navigator...");
    }

    [Fact]
    public void HandoffToAgent_FormatsWithAgentName()
    {
        var call = MakeCall("handoff_to_agent", new() { ["agentName"] = "Executor" });
        ToolStatusFormatter.Format(call).Should().Be("Handing off to Executor...");
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

    private static FunctionCallContent MakeCall(string name, Dictionary<string, object?>? args)
    {
        return new FunctionCallContent(Guid.NewGuid().ToString("N"), name, args);
    }
}
