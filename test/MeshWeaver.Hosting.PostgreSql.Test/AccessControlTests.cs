using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

namespace MeshWeaver.Hosting.PostgreSql.Test;

[Collection("PostgreSql")]
public class AccessControlTests
{
    private readonly PostgreSqlFixture _fixture;

    public AccessControlTests(PostgreSqlFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DirectUserAllowCreatesEffectivePermission()
    {
        await _fixture.CleanDataAsync();
        var ac = _fixture.AccessControl;

        await ac.GrantAsync("ACME", "alice", "Read", isAllow: true, TestContext.Current.CancellationToken);

        var hasRead = await ac.HasPermissionAsync("alice", "ACME", "Read", TestContext.Current.CancellationToken);
        hasRead.Should().BeTrue();
    }

    [Fact]
    public async Task DirectUserDenyBlocksPermission()
    {
        await _fixture.CleanDataAsync();
        var ac = _fixture.AccessControl;

        await ac.GrantAsync("ACME", "alice", "Read", isAllow: false, TestContext.Current.CancellationToken);

        var hasRead = await ac.HasPermissionAsync("alice", "ACME", "Read", TestContext.Current.CancellationToken);
        hasRead.Should().BeFalse();
    }

    [Fact]
    public async Task HierarchicalPromotionAllowOnParentGrantsChild()
    {
        await _fixture.CleanDataAsync();
        var ac = _fixture.AccessControl;

        await ac.GrantAsync("ACME", "alice", "Read", isAllow: true, TestContext.Current.CancellationToken);

        // Child path should be allowed via hierarchical promotion
        var hasRead = await ac.HasPermissionAsync("alice", "ACME/Project", "Read", TestContext.Current.CancellationToken);
        hasRead.Should().BeTrue();

        var hasReadDeep = await ac.HasPermissionAsync("alice", "ACME/Project/Story1", "Read", TestContext.Current.CancellationToken);
        hasReadDeep.Should().BeTrue();
    }

    [Fact]
    public async Task DenyOnChildBlocksSubtree()
    {
        await _fixture.CleanDataAsync();
        var ac = _fixture.AccessControl;

        // Allow on ACME, deny on ACME/Project
        await ac.GrantAsync("ACME", "alice", "Read", isAllow: true, TestContext.Current.CancellationToken);
        await ac.GrantAsync("ACME/Project", "alice", "Read", isAllow: false, TestContext.Current.CancellationToken);

        // ACME itself still allowed
        (await ac.HasPermissionAsync("alice", "ACME", "Read", TestContext.Current.CancellationToken)).Should().BeTrue();

        // ACME/Project blocked (deny is more specific)
        (await ac.HasPermissionAsync("alice", "ACME/Project", "Read", TestContext.Current.CancellationToken)).Should().BeFalse();

        // ACME/Project/Story1 also blocked (inherits from ACME/Project deny)
        (await ac.HasPermissionAsync("alice", "ACME/Project/Story1", "Read", TestContext.Current.CancellationToken)).Should().BeFalse();

        // ACME/Team still allowed (sibling not affected by ACME/Project deny)
        (await ac.HasPermissionAsync("alice", "ACME/Team", "Read", TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    [Fact]
    public async Task MostSpecificPrefixWins()
    {
        await _fixture.CleanDataAsync();
        var ac = _fixture.AccessControl;

        await ac.GrantAsync("ACME", "alice", "Update", isAllow: true, TestContext.Current.CancellationToken);
        await ac.GrantAsync("ACME/Project/Story2", "alice", "Update", isAllow: false, TestContext.Current.CancellationToken);

        // ACME/Project/Story1 allowed (inherits from ACME)
        (await ac.HasPermissionAsync("alice", "ACME/Project/Story1", "Update", TestContext.Current.CancellationToken)).Should().BeTrue();

        // ACME/Project/Story2 denied (specific deny)
        (await ac.HasPermissionAsync("alice", "ACME/Project/Story2", "Update", TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task GroupExpansionGrantsMembersAccess()
    {
        await _fixture.CleanDataAsync();
        var ac = _fixture.AccessControl;

        // Create group with members
        await ac.AddGroupMemberAsync("acme-editors", "alice", TestContext.Current.CancellationToken);
        await ac.AddGroupMemberAsync("acme-editors", "bob", TestContext.Current.CancellationToken);

        // Grant to group
        await ac.GrantAsync("ACME/Project", "acme-editors", "Read", isAllow: true, TestContext.Current.CancellationToken);

        // Both members should have access
        (await ac.HasPermissionAsync("alice", "ACME/Project", "Read", TestContext.Current.CancellationToken)).Should().BeTrue();
        (await ac.HasPermissionAsync("bob", "ACME/Project", "Read", TestContext.Current.CancellationToken)).Should().BeTrue();

        // Non-member should not
        (await ac.HasPermissionAsync("charlie", "ACME/Project", "Read", TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task RemoveGroupMemberRevokesAccess()
    {
        await _fixture.CleanDataAsync();
        var ac = _fixture.AccessControl;

        await ac.AddGroupMemberAsync("editors", "alice", TestContext.Current.CancellationToken);
        await ac.GrantAsync("ACME", "editors", "Read", isAllow: true, TestContext.Current.CancellationToken);

        (await ac.HasPermissionAsync("alice", "ACME", "Read", TestContext.Current.CancellationToken)).Should().BeTrue();

        await ac.RemoveGroupMemberAsync("editors", "alice", TestContext.Current.CancellationToken);

        (await ac.HasPermissionAsync("alice", "ACME", "Read", TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task RevokeRemovesPermission()
    {
        await _fixture.CleanDataAsync();
        var ac = _fixture.AccessControl;

        await ac.GrantAsync("ACME", "alice", "Read", isAllow: true, TestContext.Current.CancellationToken);
        (await ac.HasPermissionAsync("alice", "ACME", "Read", TestContext.Current.CancellationToken)).Should().BeTrue();

        await ac.RevokeAsync("ACME", "alice", "Read", TestContext.Current.CancellationToken);
        (await ac.HasPermissionAsync("alice", "ACME", "Read", TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task GetEffectivePermissions()
    {
        await _fixture.CleanDataAsync();
        var ac = _fixture.AccessControl;

        await ac.GrantAsync("ACME", "alice", "Read", isAllow: true, TestContext.Current.CancellationToken);
        await ac.GrantAsync("ACME", "alice", "Create", isAllow: true, TestContext.Current.CancellationToken);
        await ac.GrantAsync("ACME", "alice", "Update", isAllow: true, TestContext.Current.CancellationToken);
        await ac.GrantAsync("ACME", "alice", "Delete", isAllow: false, TestContext.Current.CancellationToken);

        var perms = await ac.GetEffectivePermissionsAsync("alice", "ACME/Project", TestContext.Current.CancellationToken);
        perms.Should().Contain("Read");
        perms.Should().Contain("Create");
        perms.Should().Contain("Update");
        perms.Should().NotContain("Delete");
    }

    [Fact]
    public async Task UserWithNoGrantsSeesEmpty()
    {
        await _fixture.CleanDataAsync();
        var ac = _fixture.AccessControl;

        var perms = await ac.GetEffectivePermissionsAsync("nobody", "ACME", TestContext.Current.CancellationToken);
        perms.Should().BeEmpty();
    }

    [Fact]
    public async Task ManualRebuildProducesSameResults()
    {
        await _fixture.CleanDataAsync();
        var ac = _fixture.AccessControl;

        await ac.GrantAsync("ACME", "alice", "Read", isAllow: true, TestContext.Current.CancellationToken);
        (await ac.HasPermissionAsync("alice", "ACME/Project", "Read", TestContext.Current.CancellationToken)).Should().BeTrue();

        // Manual rebuild
        await ac.RebuildDenormalizedTableAsync(TestContext.Current.CancellationToken);

        // Should still work
        (await ac.HasPermissionAsync("alice", "ACME/Project", "Read", TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    [Fact]
    public async Task PolicyCapsPermissions_DenyNonReadPermissions()
    {
        await _fixture.CleanDataAsync();
        var ac = _fixture.AccessControl;

        // Grant all permissions to alice at ACME
        await ac.GrantAsync("ACME", "alice", "Read", isAllow: true, TestContext.Current.CancellationToken);
        await ac.GrantAsync("ACME", "alice", "Create", isAllow: true, TestContext.Current.CancellationToken);
        await ac.GrantAsync("ACME", "alice", "Update", isAllow: true, TestContext.Current.CancellationToken);
        await ac.GrantAsync("ACME", "alice", "Delete", isAllow: true, TestContext.Current.CancellationToken);
        await ac.GrantAsync("ACME", "alice", "Comment", isAllow: true, TestContext.Current.CancellationToken);

        // Set policy: Read only (deny Create, Update, Delete, Comment)
        await ac.SetPolicyAsync("ACME", create: false, update: false, delete: false, comment: false, ct: TestContext.Current.CancellationToken);

        var perms = await ac.GetEffectivePermissionsAsync("alice", "ACME/Project", TestContext.Current.CancellationToken);
        perms.Should().Contain("Read");
        perms.Should().NotContain("Create");
        perms.Should().NotContain("Update");
        perms.Should().NotContain("Delete");
        perms.Should().NotContain("Comment");
    }

    [Fact]
    public async Task PolicyDoesNotAffectParentPath()
    {
        await _fixture.CleanDataAsync();
        var ac = _fixture.AccessControl;

        await ac.GrantAsync("ACME", "alice", "Read", isAllow: true, TestContext.Current.CancellationToken);
        await ac.GrantAsync("ACME", "alice", "Update", isAllow: true, TestContext.Current.CancellationToken);

        // Policy at child namespace only — deny all except Read
        await ac.SetPolicyAsync("ACME/Project", create: false, update: false, delete: false, comment: false, ct: TestContext.Current.CancellationToken);

        // ACME itself should still have Update
        (await ac.HasPermissionAsync("alice", "ACME", "Update", TestContext.Current.CancellationToken)).Should().BeTrue();

        // ACME/Project should only have Read
        (await ac.HasPermissionAsync("alice", "ACME/Project/Story1", "Update", TestContext.Current.CancellationToken)).Should().BeFalse();
        (await ac.HasPermissionAsync("alice", "ACME/Project/Story1", "Read", TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    [Fact]
    public async Task PolicyAffectsDescendants()
    {
        await _fixture.CleanDataAsync();
        var ac = _fixture.AccessControl;

        await ac.GrantAsync("ACME", "alice", "Read", isAllow: true, TestContext.Current.CancellationToken);
        await ac.GrantAsync("ACME", "alice", "Update", isAllow: true, TestContext.Current.CancellationToken);

        // Policy at ACME caps to Read only
        await ac.SetPolicyAsync("ACME", create: false, update: false, delete: false, comment: false, ct: TestContext.Current.CancellationToken);

        // Descendant paths should be affected
        (await ac.HasPermissionAsync("alice", "ACME/Project/Story1", "Read", TestContext.Current.CancellationToken)).Should().BeTrue();
        (await ac.HasPermissionAsync("alice", "ACME/Project/Story1", "Update", TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task PolicyRemoval_RestoresPermissions()
    {
        await _fixture.CleanDataAsync();
        var ac = _fixture.AccessControl;

        await ac.GrantAsync("ACME", "alice", "Read", isAllow: true, TestContext.Current.CancellationToken);
        await ac.GrantAsync("ACME", "alice", "Update", isAllow: true, TestContext.Current.CancellationToken);

        await ac.SetPolicyAsync("ACME", create: false, update: false, delete: false, comment: false, ct: TestContext.Current.CancellationToken);
        (await ac.HasPermissionAsync("alice", "ACME/Project", "Update", TestContext.Current.CancellationToken)).Should().BeFalse();

        // Remove policy
        await ac.RemovePolicyAsync("ACME", TestContext.Current.CancellationToken);

        // Permissions should be restored
        (await ac.HasPermissionAsync("alice", "ACME/Project", "Update", TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    [Fact]
    public async Task MultiplePoliciesAccumulate()
    {
        await _fixture.CleanDataAsync();
        var ac = _fixture.AccessControl;

        await ac.GrantAsync("ACME", "alice", "Read", isAllow: true, TestContext.Current.CancellationToken);
        await ac.GrantAsync("ACME", "alice", "Comment", isAllow: true, TestContext.Current.CancellationToken);
        await ac.GrantAsync("ACME", "alice", "Update", isAllow: true, TestContext.Current.CancellationToken);

        // Parent policy: deny Create, Update, Delete (allow Read + Comment)
        await ac.SetPolicyAsync("ACME", create: false, update: false, delete: false, ct: TestContext.Current.CancellationToken);
        // Child policy: deny all except Read
        await ac.SetPolicyAsync("ACME/Project", create: false, update: false, delete: false, comment: false, ct: TestContext.Current.CancellationToken);

        // At ACME level: Read + Comment allowed
        var acmePerms = await ac.GetEffectivePermissionsAsync("alice", "ACME/Team", TestContext.Current.CancellationToken);
        acmePerms.Should().Contain("Read");
        acmePerms.Should().Contain("Comment");
        acmePerms.Should().NotContain("Update");

        // At ACME/Project level: only Read (further restricted by child policy)
        var projectPerms = await ac.GetEffectivePermissionsAsync("alice", "ACME/Project/Story1", TestContext.Current.CancellationToken);
        projectPerms.Should().Contain("Read");
        projectPerms.Should().NotContain("Comment");
        projectPerms.Should().NotContain("Update");
    }
}
