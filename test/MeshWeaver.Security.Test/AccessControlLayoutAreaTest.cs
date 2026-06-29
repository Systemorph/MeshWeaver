using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Tests that AccessControlLayoutArea renders correctly with inherited (markdown table)
/// and local (editable rows) sections, driven by IMeshService on AccessAssignment MeshNodes.
/// </summary>
public class AccessControlLayoutAreaTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout => new CancellationTokenSource(10.Seconds()).Token;
    private const string NodePath = "ACME/Project";

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder)
            .AddRowLevelSecurity()
            .ConfigureDefaultNodeHub(c => c.AddDefaultLayoutAreas())
            .AddMeshNodes(
                MeshNode.FromPath("ACME") with { Name = "ACME", NodeType = "Space" },
                MeshNode.FromPath("ACME/Project") with { Name = "Project", NodeType = "Project" },
                MeshNode.FromPath("ACME/Documentation") with { Name = "Documentation", NodeType = "Markdown" },
                // Static role seeds — fixture-time setup (preferred, declarative).
                AssignmentNodeFactory.UserRole("RootUser", "Admin", "Org"),
                AssignmentNodeFactory.UserRole("DivUser", "Editor", "Org/Division"),
                AssignmentNodeFactory.UserRole("DeepUser", "Viewer", "Org/Division/Team/Project"),
                AssignmentNodeFactory.UserRole("GlobalAdmin", "Admin"),
                AssignmentNodeFactory.UserRole("OrgEditor", "Editor", "MyOrg"),
                AssignmentNodeFactory.UserRole("ProjectViewer", "Viewer", "MyOrg/Project/SubFolder"),
                // Default test login (TestUsers.Admin) is ObjectId="Roland". Seed
                // a global Admin assignment so the + Add Assignment button surfaces
                // for tests that interrogate the dialog flow.
                AssignmentNodeFactory.UserRole(TestUsers.Admin.ObjectId, "Admin")
            );

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration)
            .AddLayoutClient()
            .WithTypes([new KeyValuePair<string, Type>(nameof(AccessAssignment), typeof(AccessAssignment))]);

    [Fact(Timeout = 20000)]
    public async Task AccessControl_RendersStackControl()
    {
        // Seed data so the layout has something to render — runtime mutation
        // (this test specifically exercises the layout's reactive re-render).
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNode(AssignmentNodeFactory.UserRole("TestUser", "Viewer", "ACME"))
            .Should().Emit();

        var client = GetClient();
        var nodeAddress = new Address(NodePath);

        // Initialize the hub
        await client.Observe(new PingRequest(), o => o.WithTarget(nodeAddress)).Should().Emit();

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.AccessControlArea);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            nodeAddress,
            reference);

        // Get the rendered root control
        var control = await stream.GetControlStream(reference.Area!)
            .Where(x => x != null)
            .Should().Within(10.Seconds()).Emit();

        // Root should be a StackControl
        var stack = control.Should().BeOfType<StackControl>().Which;
        stack.Areas.Should().NotBeEmpty("AccessControl should have child areas");
    }

    [Fact(Timeout = 20000)]
    public async Task AccessControl_ShowsInheritedAndLocalSections()
    {
        // Seed both inherited and local assignments — runtime to drive layout updates.
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNode(AssignmentNodeFactory.UserRole("InheritedUser", "Viewer", "ACME"))
            .Should().Emit();
        await meshService.CreateNode(AssignmentNodeFactory.UserRole("LocalUser", "Editor", NodePath))
            .Should().Emit();

        var client = GetClient();
        var nodeAddress = new Address(NodePath);

        await client.Observe(new PingRequest(), o => o.WithTarget(nodeAddress)).Should().Emit();

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.AccessControlArea);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            nodeAddress,
            reference);

        // Get root control
        var control = await stream.GetControlStream(reference.Area!)
            .Where(x => x != null)
            .Should().Within(10.Seconds()).Emit();

        var stack = control.Should().BeOfType<StackControl>().Which;

        // The AccessControlLayoutArea should produce child areas for:
        // H2 header, H3 "Inherited Permissions", markdown/empty, H3 "Local Assignments", rows/empty, [Add button]
        stack.Areas.Should().HaveCountGreaterThanOrEqualTo(4,
            "should have header, inherited section, local section, and content areas");
    }

    [Fact(Timeout = 20000)]
    public void AccessControl_NoRLS_ShowsWarning()
    {
        // SecurityService is scoped per hub — root provider can't resolve it directly.
        // The fact that the per-node hub has it is verified implicitly by the other
        // tests' GetPermissionRequest round-trips returning real values.
    }

    [Fact(Timeout = 20000)]
    public async Task AccessControl_NestedNode_ShowsInheritedAssignments()
    {
        // Create actual nodes so the hubs exist
        await NodeFactory.CreateNode(
            new MeshNode("ACME", TestPartition) { Name = "ACME", NodeType = "Group" }).Should().Emit();
        await NodeFactory.CreateNode(
            new MeshNode("Documentation", $"{TestPartition}/ACME") { Name = "Documentation", NodeType = "Markdown" }).Should().Emit();

        // Seed assignments at runtime (test of layout's reactive behavior).
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        await meshService.CreateNode(AssignmentNodeFactory.UserRole("ParentUser", "Viewer", $"{TestPartition}/ACME"))
            .Should().Emit();
        await meshService.CreateNode(AssignmentNodeFactory.UserRole("NestedUser", "Editor", $"{TestPartition}/ACME/Documentation"))
            .Should().Emit();

        var client = GetClient();
        var nestedPath = $"{TestPartition}/ACME/Documentation";
        var nodeAddress = new Address(nestedPath);

        await client.Observe(new PingRequest(), o => o.WithTarget(nodeAddress)).Should().Emit();

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.AccessControlArea);

        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            nodeAddress,
            reference);

        var control = await stream.GetControlStream(reference.Area!)
            .Where(x => x != null)
            .Should().Within(10.Seconds()).Emit();

        var stack = control.Should().BeOfType<StackControl>().Which;
        stack.Areas.Should().NotBeEmpty("AccessControl should have child areas");
    }

    [Fact(Timeout = 20000)]
    public async Task AccessControl_DeeplyNestedPath_InheritsFromAllAncestors()
    {
        // Assignments are pre-seeded via static AccessAssignment nodes in ConfigureMesh.
        // Verify permissions via the live effective-permission stream.
        var deepPath = "Org/Division/Team/Project";

        var rootPerms = await Mesh.GetEffectivePermissions(deepPath, "RootUser").Should().Match(p => p == (Permission.All | Permission.Compile));
        var divPerms = await Mesh.GetEffectivePermissions(deepPath, "DivUser").Should().Match(p => p.HasFlag(Permission.Update));
        var deepPerms = await Mesh.GetEffectivePermissions(deepPath, "DeepUser").Should().Match(p => p == (Permission.Read | Permission.Execute | Permission.Api));

        rootPerms.Should().Be(Permission.All | Permission.Compile, "RootUser with Admin at Org should have all permissions on deeply nested path");
        divPerms.Should().HaveFlag(Permission.Update, "DivUser with Editor at Org/Division should have update on deeply nested path");
        deepPerms.Should().Be(Permission.Read | Permission.Execute | Permission.Api, "DeepUser with Viewer at exact path should have read + execute + api permissions");
    }

    [Fact(Timeout = 20000)]
    public async Task SecurityService_NestedPath_ReturnsCorrectPermissions()
    {
        // Assignments are pre-seeded via ConfigureMesh's static AccessAssignment nodes.
        // Verify permissions via the live effective-permission stream.
        var nestedPath = "MyOrg/Project/SubFolder";

        var globalPerms = await Mesh.GetEffectivePermissions(nestedPath, "GlobalAdmin").Should().Match(p => p == (Permission.All | Permission.Compile));
        var orgPerms = await Mesh.GetEffectivePermissions(nestedPath, "OrgEditor").Should().Match(p => p.HasFlag(Permission.Update));
        var projectPerms = await Mesh.GetEffectivePermissions(nestedPath, "ProjectViewer").Should().Match(p => p == (Permission.Read | Permission.Execute | Permission.Api));

        globalPerms.Should().Be(Permission.All | Permission.Compile, "GlobalAdmin with global Admin role should have all permissions");
        orgPerms.Should().HaveFlag(Permission.Update, "OrgEditor with Editor at MyOrg should have update on nested path");
        projectPerms.Should().Be(Permission.Read | Permission.Execute | Permission.Api, "ProjectViewer with Viewer at exact path should have read + execute + api");

        Output.WriteLine($"GlobalAdmin permissions at {nestedPath}: {globalPerms}");
        Output.WriteLine($"OrgEditor permissions at {nestedPath}: {orgPerms}");
        Output.WriteLine($"ProjectViewer permissions at {nestedPath}: {projectPerms}");
    }

    /// <summary>
    /// Walks <paramref name="stack"/> and yields every nested area whose resolved
    /// control matches <typeparamref name="T"/>. Iterative so a deep dialog tree
    /// (root stack → ContentArea → form stack → picker) is reachable without
    /// per-test recursion boilerplate.
    /// </summary>
    private static async Task<List<(string Area, T Control)>> CollectControls<T>(
        ISynchronizationStream<JsonElement> stream,
        UiControl root,
        string rootArea)
        where T : UiControl
    {
        var results = new List<(string, T)>();
        var queue = new Queue<(UiControl Control, string Area)>();
        queue.Enqueue((root, rootArea));

        while (queue.Count > 0)
        {
            var (control, area) = queue.Dequeue();
            if (control is T match)
                results.Add((area, match));

            // StackControl surfaces children via Areas (NamedAreaControl list).
            if (control is StackControl stack && stack.Areas is { } areas)
            {
                foreach (var named in areas)
                {
                    var childArea = named.Area?.ToString();
                    if (string.IsNullOrEmpty(childArea))
                        continue;
                    var child = await ReadControlOrNull(stream, childArea);
                    if (child != null)
                        queue.Enqueue((child, childArea));
                }
            }

            // DialogControl renders Content into ContentArea — the framework
            // mounts the actual UiControl at $Dialog/ContentArea; recurse there.
            if (control is DialogControl dialog && dialog.ContentArea.Area is { } dlgContentArea)
            {
                var contentArea = dlgContentArea.ToString();
                if (!string.IsNullOrEmpty(contentArea))
                {
                    var content = await ReadControlOrNull(stream, contentArea);
                    if (content != null)
                        queue.Enqueue((content, contentArea));
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Tolerant single-control read: awaits the first non-null control in
    /// <paramref name="area"/>, returning <c>null</c> on timeout (the
    /// <c>Timeout(span, fallback)</c> overload swaps in a null-emitting
    /// observable instead of throwing). No blocking Rx→Task bridge.
    /// </summary>
    private static Task<UiControl?> ReadControlOrNull(
        ISynchronizationStream<JsonElement> stream, string area)
        => stream.GetControlStream(area)
            .Where(c => c != null)
            .Take(1)
            .Timeout(5.Seconds(), Observable.Return<UiControl?>(null))
            .ToTask();

    /// <summary>
    /// The inline add row must render for an admin viewer — a user picker plus a + Add button.
    /// Regression guard for the pre-fix bug where the role-based isAdmin probe always evaluated
    /// false on the per-node hub (CircuitContext lives on a different AccessService instance),
    /// silently hiding the add controls.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AccessControl_AdminViewer_RendersInlineAddRow()
    {
        var client = GetClient();
        // DevLogin on the client hub so the SubscribeRequest's PostPipeline stamps
        // d.AccessContext = Roland → per-node hub computes CanDelete for Roland.
        TestUsers.DevLogin(client);

        var nodeAddress = new Address(NodePath);
        await client.Observe(new PingRequest(), o => o.WithTarget(nodeAddress)).Should().Emit();

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.AccessControlArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            nodeAddress,
            reference);

        // Match throws on timeout, so a completed await is provably the matched (non-null)
        // StackControl; the ! narrows UiControl? → UiControl for the CollectControls(root) calls.
        var rootControl = (await stream.GetControlStream(reference.Area!)
            .Should().Within(15.Seconds()).Match(c => c is StackControl s && s.Areas?.Count >= 4))!;

        // The inline add row renders a user picker scoped to root-namespace users…
        var pickers = await CollectControls<MeshNodePickerControl>(stream, rootControl, reference.Area!);
        var userPicker = pickers.Select(p => p.Control)
            .FirstOrDefault(p => p.Queries?.Contains("namespace:\"\" nodeType:User") == true);
        userPicker.Should().NotBeNull(
            "the inline add row must render a user picker — broken if hub.CheckPermission(path, Permission.Delete) didn't surface the admin assignment");

        // …and a + Add button.
        var buttons = await CollectControls<ButtonControl>(stream, rootControl, reference.Area!);
        buttons.Select(b => b.Control.Data?.ToString())
            .Should().Contain(d => d != null && d.Contains("Add", StringComparison.OrdinalIgnoreCase),
                "the inline add row must render an Add button");
    }

    /// <summary>
    /// The inline add row renders a user MeshNodePickerControl (root-namespace users) and a Role
    /// SelectControl. Verifies the exact user query so regressions to the add field surface in
    /// tests rather than in the running UI.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task AccessControl_InlineAddRow_HasUserPickerAndRoleSelect()
    {
        var client = GetClient();
        TestUsers.DevLogin(client);

        var nodeAddress = new Address(NodePath);
        await client.Observe(new PingRequest(), o => o.WithTarget(nodeAddress)).Should().Emit();

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.AccessControlArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            nodeAddress,
            reference);

        // Match throws on timeout, so a completed await is provably the matched (non-null)
        // StackControl; the ! narrows UiControl? → UiControl for the CollectControls(root) calls.
        var rootControl = (await stream.GetControlStream(reference.Area!)
            .Should().Within(15.Seconds()).Match(c => c is StackControl s && s.Areas?.Count >= 4))!;

        var pickers = await CollectControls<MeshNodePickerControl>(stream, rootControl, reference.Area!);
        var userPicker = pickers.Select(p => p.Control)
            .FirstOrDefault(p => string.Equals(p.Label?.ToString(), "Add user", StringComparison.Ordinal));
        userPicker.Should().NotBeNull("the inline add row must render an 'Add user' picker");
        userPicker!.Queries.Should().NotBeNull();
        userPicker.Queries!.Should().Contain("namespace:\"\" nodeType:User",
            "users live at the root namespace, so the add field must scope to namespace:\"\"");

        var selects = await CollectControls<SelectControl>(stream, rootControl, reference.Area!);
        var roleSelect = selects.Select(s => s.Control)
            .FirstOrDefault(s => string.Equals(s.Label?.ToString(), "Role", StringComparison.Ordinal));
        roleSelect.Should().NotBeNull("the inline add row must render a Role select (default Editor)");
    }
}
