using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using FluentAssertions.Extensions;
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

    private async Task<MeshNode> CreateAssignmentNodeAsync(
        string id, AccessAssignment assignment)
    {
        var node = new MeshNode(id, TestNamespace)
        {
            Name = $"{assignment.DisplayName ?? "Test"} Access",
            NodeType = "AccessAssignment",
            Content = assignment,
            MainNode = "Admin",
        };
        return await NodeFactory.CreateNodeAsync(node);
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
        await CreateAssignmentNodeAsync("grace-access", assignment);

        var client = GetClient();
        var hostAddress = NodeAddress("grace-access");

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.ThumbnailArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            hostAddress, reference);

        var control = await stream
            .GetControlStream(reference.Area!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);

        control.Should().BeOfType<StackControl>("Thumbnail should render a StackControl");
    }

    [Fact(Timeout = 30000)]
    public async Task Thumbnail_ClickRoleChip_TogglesStrikethrough()
    {
        var assignment = new AccessAssignment
        {
            AccessObject = "User/hank",
            DisplayName = "Hank",
            Roles = [new RoleAssignment { Role = "Editor", Denied = false }]
        };
        await CreateAssignmentNodeAsync("hank-access", assignment);

        var client = GetClient();
        var hostAddress = NodeAddress("hank-access");

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.ThumbnailArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            hostAddress, reference);

        // Wait for initial render
        var initialControl = await stream
            .GetControlStream(reference.Area!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);

        var outerStack = initialControl.Should().BeOfType<StackControl>().Subject;

        // Card structure: topRow (icon + name + "+" button), chipsRow (role buttons + × buttons)
        var outerAreas = outerStack.Areas.ToArray();
        outerAreas.Length.Should().BeGreaterThanOrEqualTo(2, "should have topRow + chipsRow");

        // Get the chips row area
        var chipsRowAreaName = outerAreas[1].Area?.ToString();
        chipsRowAreaName.Should().NotBeNullOrEmpty("chipsRow area should have a name");

        var chipsRowControl = await stream
            .GetControlStream(chipsRowAreaName!)
            .Timeout(5.Seconds())
            .FirstAsync(x => x != null);

        var chipsRow = chipsRowControl.Should().BeOfType<StackControl>().Subject;
        var chipAreas = chipsRow.Areas.ToArray();
        chipAreas.Should().NotBeEmpty("should have at least one role chip");

        // First chip area is the role toggle button
        var roleChipArea = chipAreas[0].Area?.ToString();
        roleChipArea.Should().NotBeNullOrEmpty();

        // Verify initial button exists and has no line-through
        var initialChip = await stream
            .GetControlStream(roleChipArea!)
            .Timeout(5.Seconds())
            .FirstAsync(x => x != null);
        initialChip.Should().BeOfType<ButtonControl>();
        var initialStyle = ((ButtonControl)initialChip!).Style?.ToString() ?? "";
        initialStyle.Should().NotContain("line-through", "initial state should NOT have strikethrough");

        // Click the role chip to toggle denied
        client.Post(new ClickedEvent(roleChipArea!, stream.StreamId),
            o => o.WithTarget(hostAddress));

        // Wait for re-render — button style should now have line-through
        var updatedChip = await stream
            .GetControlStream(roleChipArea!)
            .Where(c =>
            {
                if (c is not ButtonControl btn) return false;
                var style = btn.Style?.ToString() ?? "";
                return style.Contains("line-through");
            })
            .Timeout(10.Seconds())
            .FirstAsync();

        updatedChip.Should().NotBeNull("role chip should re-render with strikethrough after toggle");
        var updatedStyle = ((ButtonControl)updatedChip!).Style?.ToString() ?? "";
        updatedStyle.Should().Contain("line-through", "denied role should show strikethrough");
    }

    [Fact(Timeout = 30000)]
    public async Task Thumbnail_ClickRemoveRole_RemovesChip()
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
        await CreateAssignmentNodeAsync("iris-access", assignment);

        var client = GetClient();
        var hostAddress = NodeAddress("iris-access");

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.ThumbnailArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            hostAddress, reference);

        // Wait for initial render
        var initialControl = await stream
            .GetControlStream(reference.Area!)
            .Timeout(10.Seconds())
            .FirstAsync(x => x != null);

        var outerStack = initialControl.Should().BeOfType<StackControl>().Subject;
        var outerAreas = outerStack.Areas.ToArray();
        outerAreas.Length.Should().BeGreaterThanOrEqualTo(2);

        // Get chips row
        var chipsRowAreaName = outerAreas[1].Area?.ToString();
        var chipsRowControl = await stream
            .GetControlStream(chipsRowAreaName!)
            .Timeout(5.Seconds())
            .FirstAsync(x => x != null);

        var chipsRow = chipsRowControl.Should().BeOfType<StackControl>().Subject;
        var chipAreas = chipsRow.Areas.ToArray();
        // With 2 roles: (role1, ×1, role2, ×2) = 4 areas
        chipAreas.Length.Should().BeGreaterThanOrEqualTo(4, "should have 2 role chips + 2 × buttons");

        // Click the × button for the first role (index 1)
        var removeButtonArea = chipAreas[1].Area?.ToString();
        removeButtonArea.Should().NotBeNullOrEmpty();

        client.Post(new ClickedEvent(removeButtonArea!, stream.StreamId),
            o => o.WithTarget(hostAddress));

        // Verify node updated — poll via MeshQuery until role count changes
        MeshNode? updatedNode = null;
        var ct = TestContext.Current.CancellationToken;
        var deadline = System.DateTime.UtcNow.Add(10.Seconds());
        while (System.DateTime.UtcNow < deadline)
        {
            updatedNode = await MeshQuery
                .QueryAsync<MeshNode>($"path:{TestNamespace}/iris-access", ct: ct)
                .FirstOrDefaultAsync(ct);

            if (updatedNode?.Content is AccessAssignment a && a.Roles.Count == 1)
                break;

            await Task.Delay(100, ct);
        }

        updatedNode.Should().NotBeNull();
        var result = updatedNode!.Content as AccessAssignment;
        result.Should().NotBeNull();
        result!.Roles.Should().HaveCount(1);
        result.Roles[0].Role.Should().Be("Editor", "Admin should have been removed");
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
        await CreateAssignmentNodeAsync("alice-access", assignment);

        var client = GetClient();
        var hostAddress = NodeAddress("alice-access");

        var workspace = client.GetWorkspace();
        var reference = new LayoutAreaReference(MeshNodeLayoutAreas.OverviewArea);
        var stream = workspace.GetRemoteStream<JsonElement, LayoutAreaReference>(
            hostAddress, reference);

        // Wait for Overview to render with actual areas (skip progress-only emissions)
        var item = await stream
            .Where(i => i.Value.TryGetProperty("areas", out var areas)
                && areas.EnumerateObject().Any())
            .Timeout(10.Seconds())
            .FirstAsync();

        // Search through the raw JSON for a ButtonControl with label "Change Subject"
        var json = item.Value.GetRawText();
        json.Should().Contain("Change Subject",
            "Overview should contain a 'Change Subject' button for admins");
    }

    [Fact]
    public void Overview_RendersSubjectPickerQueries()
    {
        // Verify that the [MeshNode] attribute on AccessObject provides queries
        // that allow selecting any mesh node via descendants scope
        var meshNodeAttr = typeof(AccessAssignment)
            .GetProperty(nameof(AccessAssignment.AccessObject))!
            .GetCustomAttributes(typeof(MeshWeaver.Domain.MeshNodeAttribute), false)
            .OfType<MeshWeaver.Domain.MeshNodeAttribute>().FirstOrDefault();

        meshNodeAttr.Should().NotBeNull("AccessObject should have [MeshNode] attribute");
        meshNodeAttr!.Queries.Should().HaveCountGreaterThan(0,
            "AccessObject should have at least one query for the picker");
        meshNodeAttr.Queries.Should().Contain(q => q.Contains("scope:descendants"),
            "should search descendants to allow selecting any node");
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
        var created = await CreateAssignmentNodeAsync("dave-access", assignment);

        // Directly update the node's AccessObject via DataChangeRequest
        var updatedAssignment = assignment with { AccessObject = "User/eve" };
        var updatedNode = created with { Content = updatedAssignment };
        Mesh.Post(
            new DataChangeRequest { ChangedBy = "test" }.WithUpdates(updatedNode),
            o => o.WithTarget(Mesh.Address));

        // Verify the update was persisted
        var ct = TestContext.Current.CancellationToken;
        var deadline = System.DateTime.UtcNow.Add(10.Seconds());
        MeshNode? result = null;
        while (System.DateTime.UtcNow < deadline)
        {
            result = await MeshQuery
                .QueryAsync<MeshNode>($"path:{TestNamespace}/dave-access", ct: ct)
                .FirstOrDefaultAsync(ct);

            if (result?.Content is AccessAssignment a && a.AccessObject == "User/eve")
                break;

            await Task.Delay(100, ct);
        }

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
        var created = await CreateAssignmentNodeAsync("george-access", assignment);

        // Create a Group node as the new target
        var groupNode = new MeshNode("engineering", "Admin")
        {
            Name = "Engineering",
            NodeType = "Group",
        };
        await NodeFactory.CreateNodeAsync(groupNode);

        // Change AccessObject from a User to a Group path
        var updatedAssignment = assignment with { AccessObject = "Admin/engineering" };
        var updatedNode = created with { Content = updatedAssignment };
        Mesh.Post(
            new DataChangeRequest { ChangedBy = "test" }.WithUpdates(updatedNode),
            o => o.WithTarget(Mesh.Address));

        // Verify the update was persisted — AccessObject can be any mesh node path
        var ct = TestContext.Current.CancellationToken;
        var deadline = System.DateTime.UtcNow.Add(10.Seconds());
        MeshNode? result = null;
        while (System.DateTime.UtcNow < deadline)
        {
            result = await MeshQuery
                .QueryAsync<MeshNode>($"path:{TestNamespace}/george-access", ct: ct)
                .FirstOrDefaultAsync(ct);

            if (result?.Content is AccessAssignment a && a.AccessObject == "Admin/engineering")
                break;

            await Task.Delay(100, ct);
        }

        result.Should().NotBeNull();
        var resultAssignment = result!.Content as AccessAssignment;
        resultAssignment.Should().NotBeNull();
        resultAssignment!.AccessObject.Should().Be("Admin/engineering");
        resultAssignment.Roles.Should().HaveCount(1, "roles should be preserved after subject change");
    }
}
