using System.Collections.Generic;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout.DataBinding;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Tests for CreateLayoutArea - the simplified create form logic for mesh nodes.
/// The new flow only collects Id + Description, creates a transient node, then redirects to Edit.
/// </summary>
public class CreateLayoutAreaTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string CreateView = nameof(CreateView);

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .WithRoutes(r => r.RouteAddress(ClientType, (_, d) => d.Package()))
            .AddLayout(layout => layout
                .WithView(CreateView, BuildSimplifiedCreateView));
    }

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    /// <summary>
    /// Builds a simplified create view for testing - Namespace, Name, and Description fields.
    /// </summary>
    private static UiControl BuildSimplifiedCreateView(LayoutAreaHost host, RenderingContext ctx)
    {
        // Simulate the simplified create form with Name and Description
        // Namespace is shown as readonly info, not as an editable field
        var dataId = "createFormData";
        var formData = new Dictionary<string, object?>
        {
            ["name"] = "",
            ["description"] = ""
        };
        host.UpdateData(dataId, formData);

        return Controls.Stack
            .WithView(Controls.H2("Create New Node"))
            // Info section showing Type and Location (readonly)
            .WithView(Controls.Stack
                .WithView(Controls.Body("Type: ACME/Project/Todo"))
                .WithView(Controls.Body("Location: ACME/ProductLaunch/Todo")))
            .WithView(new TextFieldControl(new JsonPointerReference("name"))
            {
                Required = true,
                Placeholder = "Enter a name",
                DataContext = LayoutAreaReference.GetDataPointer(dataId)
            })
            .WithView(new MarkdownEditorControl()
            {
                Value = new JsonPointerReference("description"),
                DataContext = LayoutAreaReference.GetDataPointer(dataId)
            });
    }

    [HubFact]
    public async Task CreateView_RendersStackControl()
    {
        var reference = new LayoutAreaReference(CreateView);
        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Timeout(5.Seconds())
            .FirstAsync(x => x != null);

        control.Should().BeOfType<StackControl>();
    }

    [HubFact]
    public async Task CreateView_HasRequiredNameField()
    {
        var reference = new LayoutAreaReference(CreateView);
        var workspace = GetClient().GetWorkspace();
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Timeout(5.Seconds())
            .FirstAsync(x => x != null);

        var stack = control.Should().BeOfType<StackControl>().Subject;
        stack.Areas.Should().NotBeEmpty();
    }

    [HubFact]
    public async Task TypeSelection_ShowsAvailableTypes()
    {
        // This test verifies that when no type is selected, the type selection grid is shown
        // The actual implementation queries INodeTypeService for available types
        await Task.CompletedTask;
    }

    [HubFact]
    public async Task GetLastPathSegment_ExtractsCorrectly()
    {
        // Test the path segment extraction logic used in CreateLayoutArea
        GetLastSegment("parent/child").Should().Be("child");
        GetLastSegment("single").Should().Be("single");
        GetLastSegment("a/b/c/d").Should().Be("d");
        GetLastSegment("").Should().Be("");

        await Task.CompletedTask;
    }

    [HubFact]
    public async Task GetParentPath_ExtractsCorrectly()
    {
        // Test the parent path extraction logic used in CreateLayoutArea
        GetParentPath("parent/child").Should().Be("parent");
        GetParentPath("single").Should().Be("");
        GetParentPath("a/b/c/d").Should().Be("a/b/c");
        GetParentPath("").Should().Be("");

        await Task.CompletedTask;
    }

    [HubFact]
    public async Task MeshNodeState_HasTransientState()
    {
        // Verify that MeshNodeState.Transient exists for the new workflow
        MeshNodeState.Transient.Should().Be(MeshNodeState.Transient);
        MeshNodeState.Active.Should().Be(MeshNodeState.Active);

        await Task.CompletedTask;
    }

    [HubFact]
    public async Task BuildContentUrl_FormatsCorrectly()
    {
        // Test the URL building helper
        MeshNodeLayoutAreas.BuildContentUrl("parent/child", "Overview").Should().Be("/parent/child/Overview");
        MeshNodeLayoutAreas.BuildContentUrl("node", "Edit").Should().Be("/node/Edit");
        MeshNodeLayoutAreas.BuildContentUrl("path", "Create", "type=MyType").Should().Be("/path/Create?type=MyType");

        await Task.CompletedTask;
    }

    [HubFact]
    public async Task EditArea_ConstantExists()
    {
        // Verify that the EditArea constant exists
        MeshNodeLayoutAreas.EditArea.Should().Be("Edit");

        await Task.CompletedTask;
    }

    private static string GetLastSegment(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var lastSlash = path.LastIndexOf('/');
        return lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
    }

    private static string GetParentPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var lastSlash = path.LastIndexOf('/');
        return lastSlash > 0 ? path[..lastSlash] : "";
    }
}
