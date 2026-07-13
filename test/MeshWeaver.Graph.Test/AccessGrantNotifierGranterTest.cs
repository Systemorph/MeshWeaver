using System.Text.Json;
using MeshWeaver.Graph;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins <see cref="AccessGrantNotifier.ResolveGranterName"/> — the "who invited me" resolution that
/// turns an anonymous "you've been given access" into "{Granter} gave you access". The granter must
/// be shown by a HUMAN name (node display name, then <see cref="User.FullName"/>/<c>Email</c>), and
/// must fall back to <c>null</c> (name-less phrasing) rather than print a raw ObjectId — the exact
/// off-putting first-contact message being fixed.
/// </summary>
public class AccessGrantNotifierGranterTest
{
    private static readonly JsonSerializerOptions Options = new();

    private static MeshNode Granter(string id, string? name = null, User? content = null) =>
        new(id, "") { Name = name, Content = content };

    [Fact]
    public void PrefersNodeDisplayName()
    {
        var node = Granter("obj-123", name: "Roland Bürgi");
        Assert.Equal("Roland Bürgi", AccessGrantNotifier.ResolveGranterName(node, "obj-123", Options));
    }

    [Fact]
    public void FallsBackToUserFullName_WhenNodeNameIsJustTheObjectId()
    {
        var node = Granter("obj-123", name: "obj-123", content: new User { FullName = "Markus K." });
        Assert.Equal("Markus K.", AccessGrantNotifier.ResolveGranterName(node, "obj-123", Options));
    }

    [Fact]
    public void FallsBackToEmail_WhenNoName()
    {
        var node = Granter("obj-123", content: new User { Email = "granter@acme.com" });
        Assert.Equal("granter@acme.com", AccessGrantNotifier.ResolveGranterName(node, "obj-123", Options));
    }

    [Fact]
    public void NullWhenGranterNodeMissing()
        => Assert.Null(AccessGrantNotifier.ResolveGranterName(null, "obj-123", Options));

    [Fact]
    public void NullWhenOnlyTheRawObjectIdIsAvailable()
    {
        // Name echoes the id and there's no User content → no human name → omit the clause.
        var node = Granter("obj-123", name: "obj-123");
        Assert.Null(AccessGrantNotifier.ResolveGranterName(node, "obj-123", Options));
    }
}
