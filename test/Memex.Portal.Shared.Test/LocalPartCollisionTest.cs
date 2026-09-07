using MeshWeaver.Hosting.AspNetCore.Portal;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins the decision behind MeshWeaver#3561: a first-time sign-in whose email has no mesh
/// <c>User</c> node must not adopt a local part that already names SOMEONE ELSE'S node as its
/// partition key. On memex.meshweaver.cloud (2026-09-07) <c>rbuergi@systemorph.com</c> and
/// <c>rbuergi@icloud.com</c> both became home <c>rbuergi</c>. The comparison is by mail address,
/// case-insensitively, and only an actual owner can collide.
/// </summary>
public class LocalPartCollisionTest
{
    [Theory]
    [InlineData("rbuergi@systemorph.com", "rbuergi@icloud.com", true)]
    [InlineData("Roger@example.org", "roger@other.example", true)]
    [InlineData("rbuergi@systemorph.com", "rbuergi@systemorph.com", false)]
    [InlineData("RBuergi@Systemorph.com", "rbuergi@systemorph.com", false)]
    [InlineData(null, "rbuergi@icloud.com", false)]
    [InlineData("", "rbuergi@icloud.com", false)]
    public void ALocalPartCollidesOnlyWhenAnotherAddressOwnsIt(string? owner, string email, bool collides) =>
        UserContextMiddleware.LocalPartCollides(owner, email).Should().Be(collides,
            "the partition key is the data boundary: a taken id is refused, the owner's own address is not");
}
