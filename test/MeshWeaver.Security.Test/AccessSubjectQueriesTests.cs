using MeshWeaver.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Unit tests for <see cref="AccessSubjectQueries"/> — the single source of truth for the
/// subject-picker queries used by the Access Control UI and the <c>[MeshNode]</c> attributes
/// on <see cref="AccessAssignment.AccessObject"/> / <see cref="GroupMembership.Member"/> —
/// and for <see cref="SearchText"/>, the diacritic-insensitive in-memory matcher the picker
/// filters with (issue #213: "Burgi" must find "Bürgi").
/// </summary>
public class AccessSubjectQueriesTests
{
    [Fact]
    public void UsersQuery_TargetsRootNamespace_NotLegacyUserPartition()
    {
        // The legacy "namespace:User" shape targets the pre-V27 `user` schema (dropped) and
        // silently returns zero rows — the root cause of issue #213's empty subject picker.
        AccessSubjectQueries.Users.Should().Be("nodeType:User namespace:\"\"");
        AccessSubjectQueries.Users.Should().NotContain("namespace:User");
    }

    [Theory]
    [InlineData("rsalzmann/Games/Lolo", "nodeType:Group namespace:rsalzmann scope:subtree")]
    [InlineData("ACME", "nodeType:Group namespace:ACME scope:subtree")]
    [InlineData("", "nodeType:Group")]
    [InlineData(null, "nodeType:Group")]
    public void GroupsQuery_ScopesToPartition(string? scopePath, string expected)
        => AccessSubjectQueries.Groups(scopePath).Should().Be(expected);

    [Fact]
    public void ForScope_ReturnsUsersAndPartitionGroups()
        => AccessSubjectQueries.ForScope("ACME/Projects/X").Should().Equal(
            AccessSubjectQueries.Users,
            "nodeType:Group namespace:ACME scope:subtree");

    [Theory]
    [InlineData("rsalzmann/Games/Lolo/_Access/alice_Access", "rsalzmann/Games/Lolo")]
    [InlineData("ACME/_Access/bob_Access", "ACME")]
    [InlineData("_Access/root_Access", "")]
    [InlineData("Admin/_Access", "Admin")]
    [InlineData("ACME/Projects/X", "ACME/Projects/X")] // no _Access segment → path IS the scope
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ScopeOfAssignment_StripsAccessSatelliteSuffix(string? assignmentPath, string expected)
        => AccessSubjectQueries.ScopeOfAssignment(assignmentPath).Should().Be(expected);

    [Fact]
    public void ScopeOfAssignment_IgnoresNonSegmentOccurrences()
        // "_AccessLog" is not the "_Access" satellite segment — must not be treated as one.
        => AccessSubjectQueries.ScopeOfAssignment("ACME/_AccessLog/x").Should().Be("ACME/_AccessLog/x");

    [Theory]
    [InlineData("ACME/Projects/X", "ACME")]
    [InlineData("rbuergi", "rbuergi")]
    [InlineData("/rbuergi/", "rbuergi")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Partition_IsFirstSegment(string? path, string expected)
        => AccessSubjectQueries.Partition(path).Should().Be(expected);

    [Fact]
    public void AccessAssignmentAttribute_UsesCanonicalQueries()
    {
        // Pin the [MeshNode] attribute on AccessAssignment.AccessObject to the canonical
        // constants — a drift back to a hand-rolled query re-opens issue #213.
        var attr = typeof(AccessAssignment).GetProperty(nameof(AccessAssignment.AccessObject))!
            .GetCustomAttributes(typeof(MeshNodeAttribute), false)
            .OfType<MeshNodeAttribute>().Single();
        attr.Queries.Should().Equal(AccessSubjectQueries.Users, AccessSubjectQueries.GroupsTemplate);
    }

    [Fact]
    public void GroupMembershipAttribute_UsesCanonicalUsersQuery()
    {
        var attr = typeof(GroupMembership).GetProperty(nameof(GroupMembership.Member))!
            .GetCustomAttributes(typeof(MeshNodeAttribute), false)
            .OfType<MeshNodeAttribute>().Single();
        attr.Queries[0].Should().Be(AccessSubjectQueries.Users);
    }
}

/// <summary>
/// Unit tests for <see cref="SearchText"/> — diacritic folding and matching.
/// </summary>
public class SearchTextTests
{
    [Theory]
    [InlineData("Bürgi", "burgi")]
    [InlineData("BÜRGI", "burgi")]
    [InlineData("Müller-Straße", "muller-strasse")]
    [InlineData("Ærø", "aero")]
    [InlineData("Łódź", "lodz")]
    [InlineData("café", "cafe")]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Fold_RemovesDiacriticsAndCase(string? input, string expected)
        => SearchText.Fold(input).Should().Be(expected);

    [Theory]
    [InlineData("Burgi", "Roland Bürgi")]   // unaccented query finds accented name
    [InlineData("Bürgi", "Roland Burgi")]   // accented query finds unaccented name
    [InlineData("bürgi", "Roland BÜRGI")]
    [InlineData("roland b", "Roland Bürgi")]
    [InlineData("", "anything")]            // empty search matches everything
    public void Matches_IsDiacriticAndCaseInsensitive(string search, string name)
        => SearchText.Matches(search, name).Should().BeTrue();

    [Theory]
    [InlineData("Bürge", "Roland Bürgi")]   // different letters stay different (e ≠ i)
    [InlineData("xyz", "Roland Bürgi")]
    public void Matches_RejectsNonMatches(string search, string name)
        => SearchText.Matches(search, name).Should().BeFalse();

    [Fact]
    public void Matches_ChecksEveryField()
    {
        SearchText.Matches("bürgi", null, "path/rburgi", null).Should().BeTrue();
        SearchText.Matches("bürgi", null, null, "User").Should().BeFalse();
    }
}
