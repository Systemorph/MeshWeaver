using System;
using System.Reactive.Linq;
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

    /// <summary>
    /// 🚨 <see cref="RecentlyDeletedRegistry.WithinSubtreeDeletion{T}"/> holds the scope while the body
    /// runs and releases it BEFORE the body's value reaches the subscriber — the value is the caller's
    /// "done" signal, and a caller acting on it must not meet the scope still held. Under a bare
    /// <c>Observable.Using</c> the third assertion fails: Rx disposes the resource only after the
    /// observer has processed the value.
    /// </summary>
    [Fact]
    public void WithinSubtreeDeletion_holds_while_the_body_runs_and_releases_before_the_value_is_delivered()
    {
        var registry = new RecentlyDeletedRegistry();
        bool? heldInBody = null;
        bool? heldAtDelivery = null;
        var completed = false;

        using var subscription = registry.WithinSubtreeDeletion("s/t", () =>
            {
                heldInBody = registry.IsUnderActiveDeletion("s/t/u", out _);
                return Observable.Return(42);
            })
            .Subscribe(value => heldAtDelivery = registry.IsUnderActiveDeletion("s/t/u", out _), () => completed = true);

        heldInBody.Should().BeTrue("the scope opens before the body is subscribed");
        completed.Should().BeTrue();
        heldAtDelivery.Should().BeFalse("the scope is released before the value reaches the subscriber");
    }

    [Fact]
    public void WithinSubtreeDeletion_releases_before_an_error_is_delivered()
    {
        var registry = new RecentlyDeletedRegistry();
        bool? heldAtError = null;

        using var subscription = registry.WithinSubtreeDeletion("e", () => Observable.Throw<int>(new InvalidOperationException("boom")))
            .Subscribe(_ => { }, ex => heldAtError = registry.IsUnderActiveDeletion("e/f", out _));

        heldAtError.Should().BeFalse("a retry issued on the failure must not be refused as in flight");
    }

    [Fact]
    public void WithinSubtreeDeletion_releases_on_unsubscribe()
    {
        var registry = new RecentlyDeletedRegistry();
        var subscription = registry.WithinSubtreeDeletion("n", Observable.Never<int>).Subscribe(_ => { });
        registry.IsUnderActiveDeletion("n/m", out _).Should().BeTrue("the body has not terminated");

        subscription.Dispose();
        registry.IsUnderActiveDeletion("n/m", out _).Should().BeFalse("unsubscribing releases the scope");
    }
}
