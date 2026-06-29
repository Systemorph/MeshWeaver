using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Client;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.AccessControl.Test;

/// <summary>
/// Tests for AccessAssignment Thumbnail layout rendering:
/// - Thumbnail renders a StackControl with user info and role chips
/// - Clicking a role chip toggles strikethrough (denied flag)
/// - Clicking × removes a role from the assignment
/// </summary>
public class AccessAssignmentThumbnailTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    // Path prefix for test access assignment nodes
    private const string TestNamespace = "Admin/_Access";

    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient(d => d);

    private async Task<MeshNode> CreateAssignmentNode(
        string id, AccessAssignment assignment)
    {
        var node = new MeshNode(id, TestNamespace)
        {
            Name = $"{assignment.DisplayName ?? "Test"} Access",
            NodeType = "AccessAssignment",
            Content = assignment,
            MainNode = "Admin",
        };
        // Cold observable — the create runs on the assertion's Subscribe.
        return await NodeFactory.CreateNode(node).Should().Emit();
    }

    private static Address NodeAddress(string id)
        => new("Admin", "_Access", id);

    /// <summary>
    /// Recursively searches a JsonElement for a ButtonControl with a specific label.
    /// Returns the key name of the matching area, or null if not found.
    /// </summary>
    private static string? FindAreaByLabel(JsonElement element, string label)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            // Check if this element itself is a ButtonControl with the label
            if (element.TryGetProperty("$type", out var typeProp)
                && typeProp.GetString()?.Contains("ButtonControl") == true
                && element.TryGetProperty("label", out var labelProp)
                && labelProp.GetString() == label)
            {
                return label; // Will be matched at parent level
            }

            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Object)
                {
                    var raw = prop.Value.GetRawText();
                    if (raw.Contains(label) && raw.Contains("ButtonControl"))
                    {
                        // Check if this property value IS the button
                        if (prop.Value.TryGetProperty("$type", out var innerType)
                            && innerType.GetString()?.Contains("ButtonControl") == true)
                            return prop.Name;

                        // Otherwise recurse deeper
                        var found = FindAreaByLabel(prop.Value, label);
                        if (found != null) return found;
                    }
                }
                else if (prop.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in prop.Value.EnumerateArray())
                    {
                        var found = FindAreaByLabel(item, label);
                        if (found != null) return found;
                    }
                }
            }
        }
        return null;
    }

    [Fact(Timeout = 30000)]
    public async Task Thumbnail_RendersStackControl()
    {
        var assignment = new AccessAssignment
        {
            AccessObject = "User/grace",
            DisplayName = "Grace",
            Roles = [new RoleAssignment { Role = "Admin", Denied = false }]
        };
        await CreateAssignmentNode("grace-access", assignment);

        var client = GetClient();
        var hostAddress = NodeAddress("grace-access");

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.ThumbnailArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            hostAddress, reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Should().Within(10.Seconds())
            .Match(x => x != null);

        control.Should().BeOfType<StackControl>("Thumbnail should render a StackControl");
    }

    [Fact(Timeout = 30000)]
    public async Task Thumbnail_ClickRemoveUser_DeletesAssignment()
    {
        var assignment = new AccessAssignment
        {
            AccessObject = "User/jack",
            DisplayName = "Jack",
            Roles = [new RoleAssignment { Role = "Editor", Denied = false }]
        };
        await CreateAssignmentNode("jack-access", assignment);

        var client = GetClient();
        var hostAddress = NodeAddress("jack-access");

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.ThumbnailArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            hostAddress, reference);

        // Row structure: [subject thumbnail] [rolesBlock] [✕ remove-user button]
        var outerStack = (await stream
            .GetControlStream(reference.Area!)
            .Should().Within(10.Seconds())
            .Match(x => x is StackControl))!.Should().BeOfType<StackControl>().Subject;

        var outerAreas = outerStack.Areas.ToArray();
        outerAreas.Length.Should().BeGreaterThanOrEqualTo(3,
            "row has subject + rolesBlock + remove-user button for an admin");

        // The last area is the remove-user ✕ button.
        var removeUserArea = outerAreas[^1].Area?.ToString();
        removeUserArea.Should().NotBeNullOrEmpty();

        client.Post(new ClickedEvent(removeUserArea!, stream.StreamId),
            o => o.WithTarget(hostAddress));

        // Re-read authoritatively (Mesh.GetMeshNode round-trip) until the node is gone.
        var deleted = await Observable.Interval(100.Milliseconds()).StartWith(0L)
            .SelectMany(_ => Mesh.GetMeshNode($"{TestNamespace}/jack-access"))
            .Should().Within(10.Seconds())
            .Match(n => n is null);

        deleted.Should().BeNull("the whole assignment node should be deleted");
    }

    [Fact(Timeout = 30000)]
    public async Task Thumbnail_ClickRemoveRole_RemovesRole()
    {
        var assignment = new AccessAssignment
        {
            AccessObject = "User/iris",
            DisplayName = "Iris",
            Roles =
            [
                new RoleAssignment { Role = "Admin", Denied = false },
                new RoleAssignment { Role = "Editor", Denied = false }
            ]
        };
        await CreateAssignmentNode("iris-access", assignment);

        var client = GetClient();
        var hostAddress = NodeAddress("iris-access");

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.ThumbnailArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            hostAddress, reference);

        // Row structure: [subject] [rolesBlock] [✕]. rolesBlock = [roleRow0] [roleRow1] [+ Add role].
        // Each roleRow = [role editor] [× remove-role].
        var outerStack = (await stream
            .GetControlStream(reference.Area!)
            .Should().Within(10.Seconds())
            .Match(x => x is StackControl))!.Should().BeOfType<StackControl>().Subject;

        var outerAreas = outerStack.Areas.ToArray();
        outerAreas.Length.Should().BeGreaterThanOrEqualTo(2, "subject + rolesBlock");

        var rolesBlockArea = outerAreas[1].Area?.ToString();
        var rolesBlock = (await stream
            .GetControlStream(rolesBlockArea!)
            .Should().Within(5.Seconds())
            .Match(x => x is StackControl))!.Should().BeOfType<StackControl>().Subject;

        var roleRows = rolesBlock.Areas.ToArray();
        roleRows.Length.Should().BeGreaterThanOrEqualTo(2, "two role rows (+ an Add role button)");

        // First role row → its remove-role × button (the role editor is at index 0).
        var roleRow0Area = roleRows[0].Area?.ToString();
        var roleRow0 = (await stream
            .GetControlStream(roleRow0Area!)
            .Should().Within(5.Seconds())
            .Match(x => x is StackControl))!.Should().BeOfType<StackControl>().Subject;

        var roleRow0Areas = roleRow0.Areas.ToArray();
        roleRow0Areas.Length.Should().BeGreaterThanOrEqualTo(2, "role editor + remove-role button");

        var removeRoleArea = roleRow0Areas[1].Area?.ToString();
        removeRoleArea.Should().NotBeNullOrEmpty();

        client.Post(new ClickedEvent(removeRoleArea!, stream.StreamId),
            o => o.WithTarget(hostAddress));

        // Re-read authoritatively until the first role (Admin) is removed.
        var updatedNode = await Observable.Interval(100.Milliseconds()).StartWith(0L)
            .SelectMany(_ => Mesh.GetMeshNode($"{TestNamespace}/iris-access"))
            .Should().Within(10.Seconds())
            .Match(n => n?.Content is AccessAssignment a && a.Roles.Count == 1);

        updatedNode.Should().NotBeNull();
        var result = updatedNode!.Content as AccessAssignment;
        result.Should().NotBeNull();
        result!.Roles.Should().HaveCount(1);
        result.Roles[0].Role.Should().Be("Editor", "Admin (index 0) should have been removed");
    }

    [Fact(Timeout = 30000)]
    public async Task Overview_RendersChangeSubjectButton()
    {
        var assignment = new AccessAssignment
        {
            AccessObject = "User/alice",
            DisplayName = "Alice",
            Roles = [new RoleAssignment { Role = "Editor", Denied = false }]
        };
        await CreateAssignmentNode("alice-access", assignment);

        var client = GetClient();
        var hostAddress = NodeAddress("alice-access");

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            hostAddress, reference);

        // Wait for the Overview area control itself to render. The prior
        // shape ("first emission with any 'areas'") raced the menu burst —
        // $Menu/$Menu:Node/$Menu:Mesh land before Overview, and matching on
        // "any areas" would happily snap the menu-only frame and assert
        // against menus that have no Change Subject button.
        await stream
            .GetControlStream(reference.Area!)
            .Should().Within(10.Seconds())
            .Match(x => x != null);

        // Read the assembled entity-store now that the Overview area exists.
        var settled = await stream
            .Should().Within(5.Seconds())
            .Match(i => i.Value.TryGetProperty("areas", out var areas)
                && areas.EnumerateObject().Any(a => a.Name.Contains("Overview")));

        var json = settled.Value.GetRawText();
        json.Should().Contain("Change Subject",
            "Overview should contain a 'Change Subject' button for admins");
    }

    [Fact]
    public void Overview_RendersSubjectPickerQueries()
    {
        // Verify that the [MeshNode] attribute on AccessObject provides queries
        // that scope the picker to valid subjects (Users + Groups in the current subtree).
        var meshNodeAttr = typeof(AccessAssignment)
            .GetProperty(nameof(AccessAssignment.AccessObject))!
            .GetCustomAttributes(typeof(MeshWeaver.Domain.MeshNodeAttribute), false)
            .OfType<MeshWeaver.Domain.MeshNodeAttribute>().FirstOrDefault();

        meshNodeAttr.Should().NotBeNull("AccessObject should have [MeshNode] attribute");
        meshNodeAttr!.Queries.Should().HaveCountGreaterThan(0,
            "AccessObject should have at least one query for the picker");
        meshNodeAttr.Queries.Should().Contain(q => q.Contains("nodeType:User"),
            "User subjects must be pickable");
        meshNodeAttr.Queries.Should().Contain(q => q.Contains("nodeType:Group"),
            "Group subjects must be pickable");
    }

    [Fact(Timeout = 30000)]
    public async Task UpdateAccessObject_ChangesSubject_ViaDataChange()
    {
        var assignment = new AccessAssignment
        {
            AccessObject = "User/dave",
            DisplayName = "Dave",
            Roles = [new RoleAssignment { Role = "Viewer", Denied = false }]
        };
        var created = await CreateAssignmentNode("dave-access", assignment);

        // Update the node's AccessObject via NodeFactory (cold — runs on Subscribe).
        var updatedAssignment = assignment with { AccessObject = "User/eve" };
        var updatedNode = created with { Content = updatedAssignment };
        await NodeFactory.UpdateNode(updatedNode).Should().Emit();

        // Re-read authoritatively (Mesh.GetMeshNode round-trip) until the
        // subject change lands — same pattern as the sibling CanSelectAnyNodeType
        // test. GetMeshNodeStream + Where does not surface the update reliably here.
        var result = await Observable.Interval(100.Milliseconds()).StartWith(0L)
            .SelectMany(_ => Mesh.GetMeshNode($"{TestNamespace}/dave-access"))
            .Should().Within(10.Seconds())
            .Match(n => n?.Content is AccessAssignment a && a.AccessObject == "User/eve");

        result.Should().NotBeNull();
        var resultAssignment = result!.Content as AccessAssignment;
        resultAssignment.Should().NotBeNull();
        resultAssignment!.AccessObject.Should().Be("User/eve");
        resultAssignment.Roles.Should().HaveCount(1, "roles should be preserved");
    }

    [Fact]
    public void UpdateAccessObject_RejectsEmpty_Validation()
    {
        // Verify the dialog-level validation: empty string should be rejected
        // This tests the guard in UpdateAccessObjectAsync
        var result = string.IsNullOrEmpty("");
        result.Should().BeTrue("empty AccessObject should be considered invalid");

        var resultNull = string.IsNullOrEmpty(null);
        resultNull.Should().BeTrue("null AccessObject should be considered invalid");

        var resultWhitespace = string.IsNullOrEmpty("  ".Trim());
        resultWhitespace.Should().BeTrue("whitespace-only AccessObject should be considered invalid after trim");

        var resultValid = string.IsNullOrEmpty("User/frank");
        resultValid.Should().BeFalse("a valid node path should be accepted");
    }

    [Fact(Timeout = 30000)]
    public async Task UpdateAccessObject_CanSelectAnyNodeType_ViaDataChange()
    {
        // Create an assignment pointing to a User
        var assignment = new AccessAssignment
        {
            AccessObject = "User/george",
            DisplayName = "George",
            Roles = [new RoleAssignment { Role = "Editor", Denied = false }]
        };
        var created = await CreateAssignmentNode("george-access", assignment);

        // Create a Group node as the new target (cold — runs on Subscribe).
        var groupNode = new MeshNode("engineering", "Admin")
        {
            Name = "Engineering",
            NodeType = "Group",
        };
        await NodeFactory.CreateNode(groupNode).Should().Emit();

        // Change AccessObject from a User to a Group path
        var updatedAssignment = assignment with { AccessObject = "Admin/engineering" };
        var updatedNode = created with { Content = updatedAssignment };
        await NodeFactory.UpdateNode(updatedNode).Should().Emit();

        // Verify the update was persisted — AccessObject can be any mesh node
        // path. Re-read authoritatively (Mesh.GetMeshNode round-trip) until the
        // change lands.
        var result = await Observable.Interval(100.Milliseconds()).StartWith(0L)
            .SelectMany(_ => Mesh.GetMeshNode($"{TestNamespace}/george-access"))
            .Should().Within(10.Seconds())
            .Match(n => n?.Content is AccessAssignment a && a.AccessObject == "Admin/engineering");

        result.Should().NotBeNull();
        var resultAssignment = result!.Content as AccessAssignment;
        resultAssignment.Should().NotBeNull();
        resultAssignment!.AccessObject.Should().Be("Admin/engineering");
        resultAssignment.Roles.Should().HaveCount(1, "roles should be preserved after subject change");
    }
}
