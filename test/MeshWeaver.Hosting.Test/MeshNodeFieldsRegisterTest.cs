using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MeshWeaver.Mesh;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>The register a retired-field build error is decided from must be TRUE to the type</b>
/// (#6638). <c>.github/meshnode-fields.json</c> lists <see cref="MeshNode"/>'s JSON members and the
/// members it no longer has; <c>check-retired-node-fields.py</c> (core's trees and every node
/// repo's validate lane) turns a node file carrying a retired member into a red PR, because
/// GitSync's strict reader refuses the whole snapshot on such a file. The gate is Python and cannot
/// reflect, so this test is what keeps the register honest in both directions:
/// <list type="bullet">
///   <item><c>members</c> equals the type's JSON members — so removing a member from
///     <see cref="MeshNode"/> fails HERE until its name is moved into <c>retired</c>, and the
///     content that still carries it starts failing the gate the same day;</item>
///   <item>no <c>retired</c> name is a live member — a register that would red a valid file is
///     wrong, never the file.</item>
/// </list>
/// </summary>
public class MeshNodeFieldsRegisterTest
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    [Fact]
    public void TheRegisterMembersAreExactlyMeshNodesJsonMembers()
    {
        var (members, _) = ReadRegister();
        var actual = JsonMembers().ToHashSet(StringComparer.Ordinal);

        var added = actual.Except(members).ToArray();
        var removed = members.Except(actual).ToArray();
        added.Should().BeEmpty(
            $"a member added to MeshNode must be added to .github/meshnode-fields.json `members` (missing: {string.Join(", ", added)})");
        removed.Should().BeEmpty(
            $"(no longer members: {string.Join(", ", removed)}) "
            + "a member REMOVED from MeshNode must move from `members` to `retired` in "
            + ".github/meshnode-fields.json, so every node file still carrying it fails the "
            + "retired-field gate instead of refusing a GitSync snapshot in production (#6638)");
    }

    [Fact]
    public void NoRetiredNameIsALiveMember()
    {
        var (_, retired) = ReadRegister();
        var actual = JsonMembers()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        retired.Should().NotBeEmpty("an empty register makes the gate pass vacuously");
        retired.Where(actual.Contains).Should().BeEmpty(
            "a retired name that is still a member would red every valid file carrying it");
    }

    /// <summary>MeshNode's serialized JSON members: the contract's properties minus the
    /// <c>[JsonIgnore]</c>d ones (the default resolver lists those too, with no accessor).</summary>
    private static System.Collections.Generic.IEnumerable<string> JsonMembers() =>
        Web.GetTypeInfo(typeof(MeshNode)).Properties
            .Where(p => p.AttributeProvider?.IsDefined(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), true) != true)
            .Select(p => p.Name);

    private static (string[] Members, string[] Retired) ReadRegister()
    {
        var path = Path.Combine(RepoPaths.Root, ".github", "meshnode-fields.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var members = doc.RootElement.GetProperty("members").EnumerateArray().Select(e => e.GetString()!).ToArray();
        var retired = doc.RootElement.GetProperty("retired").EnumerateObject().Select(p => p.Name).ToArray();
        return (members, retired);
    }
}
