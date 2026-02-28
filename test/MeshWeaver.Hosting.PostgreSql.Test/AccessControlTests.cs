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

        await ac.GrantAsync("ACME", "alice", "Read", isAllow: true);

        var hasRead = await ac.HasPermissionAsync("alice", "ACME", "Read");
        hasRead.Should().BeTrue();
    }

    [Fact]
    public async Task DirectUserDenyBlocksPermission()
    {
        await _fixture.CleanDataAsync();
        var ac = _fixture.AccessControl;

        await ac.GrantAsync("ACME", "alice", "Read", isAllow: false);

        var hasRead = await ac.HasPermissionAsync("alice", "ACME", "Read");
        hasRead.Should().BeFalse();
    }

    [Fact]
    public async Task HierarchicalPromotionAllowOnParentGrantsChild()
    {
        await _fixture.CleanDataAsync();
        var ac = _fixture.AccessControl;

        await ac.GrantAsync("ACME", "alice", "Read", isAllow: true);

        // Child path should be allowed via hierarchical promotion
        var hasRead = await ac.HasPermissionAsync("alice", "ACME/Project", "Read");
        hasRead.Should().BeTrue();

        var hasReadDeep = await ac.HasPermissionAsync("alice", "ACME/Project/Story1", "Read");
        hasReadDeep.Should().BeTrue();
    }

    [Fact]
    public async Task DenyOnChildBlocksSubtree()
    {
        await _fixture.CleanDataAsync();
        var ac = _fixture.AccessControl;

        // Allow on ACME, deny on ACME/Project
        await ac.GrantAsync("ACME", "alice", "Read", isAllow: true);
        await ac.GrantAsync("ACME/Project", "alice", "Read", isAllow: false);

        // ACME itself still allowed
        (await ac.HasPermissionAsync("alice", "ACME", "Read")).Should().BeTrue();

        // ACME/Project blocked (deny is more specific)
        (await ac.HasPermissionAsync("alice", "ACME/Project", "Read")).Should().BeFalse();

        // ACME/Project/Story1 also blocked (inherits from ACME/Project deny)
        (await ac.HasPermissionAsync("alice", "ACME/Project/Story1", "Read")).Should().BeFalse();

        // ACME/Team still allowed (sibling not affected by ACME/Project deny)
        (await ac.HasPermissionAsync("alice", "ACME/Team", "Read")).Should().BeTrue();
    }

    [Fact]
    public async Task MostSpecificPrefixWins()
    {
        await _fixture.CleanDataAsync();
        var ac = _fixture.AccessControl;

        await ac.GrantAsync("ACME", "alice", "Update", isAllow: true);
        await ac.GrantAsync("ACME/Project/Story2", "alice", "Update", isAllow: false);

        // ACME/Project/Story1 allowed (inherits from ACME)
        (await ac.HasPermissionAsync("alice", "ACME/Project/Story1", "Update")).Should().BeTrue();

        // ACME/Project/Story2 denied (specific deny)
        (await ac.HasPermissionAsync("alice", "ACME/Project/Story2", "Update")).Should().BeFalse();
    }

    [Fact]
    public async Task GroupExpansionGrantsMembersAccess()
    {
        await _fixture.CleanDataAsync();
        var ac = _fixture.AccessControl;

        // Create group with members
        await ac.AddGroupMemberAsync("acme-editors", "alice");
        await ac.AddGroupMemberAsync("acme-editors", "bob");

        // Grant to group
        await ac.GrantAsync("ACME/Project", "acme-editors", "Read", isAllow: true);

        // Both members should have access
        (await ac.HasPermissionAsync("alice", "ACME/Project", "Read")).Should().BeTrue();
        (await ac.HasPermissionAsync("bob", "ACME/Project", "Read")).Should().BeTrue();

        // Non-member should not
        (await ac.HasPermissionAsync("charlie", "ACME/Project", "Read")).Should().BeFalse();
    }

    [Fact]
    public async Task RemoveGroupMemberRevokesAccess()
    {
        await _fixture.CleanDataAsync();
        var ac = _fixture.AccessControl;

        await ac.AddGroupMemberAsync("editors", "alice");
        await ac.GrantAsync("ACME", "editors", "Read", isAllow: true);

        (await ac.HasPermissionAsync("alice", "ACME", "Read")).Should().BeTrue();

        await ac.RemoveGroupMemberAsync("editors", "alice");

        (await ac.HasPermissionAsync("alice", "ACME", "Read")).Should().BeFalse();
    }

    [Fact]
    public async Task RevokeRemovesPermission()
    {
        await _fixture.CleanDataAsync();
        var ac = _fixture.AccessControl;

        await ac.GrantAsync("ACME", "alice", "Read", isAllow: true);
        (await ac.HasPermissionAsync("alice", "ACME", "Read")).Should().BeTrue();

        await ac.RevokeAsync("ACME", "alice", "Read");
        (await ac.HasPermissionAsync("alice", "ACME", "Read")).Should().BeFalse();
    }

    [Fact]
    public async Task GetEffectivePermissions()
    {
        await _fixture.CleanDataAsync();
        var ac = _fixture.AccessControl;

        await ac.GrantAsync("ACME", "alice", "Read", isAllow: true);
        await ac.GrantAsync("ACME", "alice", "Create", isAllow: true);
        await ac.GrantAsync("ACME", "alice", "Update", isAllow: true);
        await ac.GrantAsync("ACME", "alice", "Delete", isAllow: false);

        var perms = await ac.GetEffectivePermissionsAsync("alice", "ACME/Project");
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

        var perms = await ac.GetEffectivePermissionsAsync("nobody", "ACME");
        perms.Should().BeEmpty();
    }

    [Fact]
    public async Task ManualRebuildProducesSameResults()
    {
        await _fixture.CleanDataAsync();
        var ac = _fixture.AccessControl;

        await ac.GrantAsync("ACME", "alice", "Read", isAllow: true);
        (await ac.HasPermissionAsync("alice", "ACME/Project", "Read")).Should().BeTrue();

        // Manual rebuild
        await ac.RebuildDenormalizedTableAsync();

        // Should still work
        (await ac.HasPermissionAsync("alice", "ACME/Project", "Read")).Should().BeTrue();
    }
}
