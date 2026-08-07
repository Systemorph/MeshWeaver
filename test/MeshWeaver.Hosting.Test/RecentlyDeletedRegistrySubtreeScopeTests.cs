using System;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// Unit tests for the active-subtree-deletion scope on
/// <see cref="RecentlyDeletedRegistry"/> — the hard invariant (no timer) the
/// storage write guard enforces while a recursive delete is in flight
/// (issue #839: mid-flight creations under a subtree being deleted).
/// </summary>
public class RecentlyDeletedRegistrySubtreeScopeTests
{
    [Fact]
    public void Scope_covers_root_and_descendants_only()
    {
        var registry = new RecentlyDeletedRegistry();
        using var scope = registry.BeginSubtreeDeletion("a/b");

        registry.IsUnderActiveDeletion("a/b", out var root1).Should().BeTrue();
        root1.Should().Be("a/b");
        registry.IsUnderActiveDeletion("a/b/c", out _).Should().BeTrue();
        registry.IsUnderActiveDeletion("a/b/c/d", out _).Should().BeTrue();

        // Ancestors and prefix-sharing SIBLINGS are outside the scope — the boundary
        // is the path separator, never a raw string prefix.
        registry.IsUnderActiveDeletion("a", out _).Should().BeFalse();
        registry.IsUnderActiveDeletion("a/bc", out _).Should().BeFalse();
        registry.IsUnderActiveDeletion("a/bc/d", out _).Should().BeFalse();
        registry.IsUnderActiveDeletion("unrelated", out _).Should().BeFalse();
    }

    [Fact]
    public void Dispose_releases_the_scope()
    {
        var registry = new RecentlyDeletedRegistry();
        var scope = registry.BeginSubtreeDeletion("x/y");
        registry.IsUnderActiveDeletion("x/y/z", out _).Should().BeTrue();

        scope.Dispose();
        registry.IsUnderActiveDeletion("x/y/z", out _).Should().BeFalse();

        // Idempotent — a second Dispose must not throw or corrupt the ref-count.
        scope.Dispose();
        registry.IsUnderActiveDeletion("x/y/z", out _).Should().BeFalse();
    }

    [Fact]
    public void Concurrent_scopes_on_same_root_are_refcounted()
    {
        var registry = new RecentlyDeletedRegistry();
        var first = registry.BeginSubtreeDeletion("p");
        var second = registry.BeginSubtreeDeletion("p");

        first.Dispose();
        registry.IsUnderActiveDeletion("p/q", out _).Should().BeTrue(
            "the second concurrent delete still holds the scope");

        second.Dispose();
        registry.IsUnderActiveDeletion("p/q", out _).Should().BeFalse();
    }

    [Fact]
    public void Empty_root_is_a_noop_scope()
    {
        var registry = new RecentlyDeletedRegistry();
        using var scope = registry.BeginSubtreeDeletion("");
        registry.IsUnderActiveDeletion("anything", out _).Should().BeFalse();
    }
}
