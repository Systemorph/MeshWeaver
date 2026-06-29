using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Graph.Configuration;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Unit tests for <see cref="CodeQueryResolver"/>. The resolver is shared between
/// the compiler (deciding which Code nodes to compile into a NodeType) and the
/// Configuration side menu (showing those same files under Sources / Tests), so
/// these rules need locked-down behaviour to keep the two in sync.
/// </summary>
public class CodeQueryResolverTest
{
    private const string SelfPath = "Acme/Project";

    [Fact]
    public void Expand_BareNamespace_RebasesOntoSelfPath()
    {
        var result = CodeQueryResolver.Expand("namespace:Source scope:subtree", SelfPath).ToList();
        result.Should().ContainSingle()
            .Which.Should().Be($"namespace:{SelfPath}/Source scope:subtree nodeType:Code");
    }

    [Fact]
    public void Expand_QualifiedNamespace_IsLeftAlone()
    {
        var result = CodeQueryResolver.Expand("namespace:Other/Lib/Source scope:subtree", SelfPath).ToList();
        result.Should().ContainSingle()
            .Which.Should().Be("namespace:Other/Lib/Source scope:subtree nodeType:Code");
    }

    [Fact]
    public void Expand_DollarSelfMacro_ExpandsToSelfPath()
    {
        var result = CodeQueryResolver.Expand("namespace:$self/Source scope:subtree", SelfPath).ToList();
        result.Should().ContainSingle()
            .Which.Should().Be($"namespace:{SelfPath}/Source scope:subtree nodeType:Code");
    }

    [Fact]
    public void Expand_AtShorthand_YieldsBothPathAndNamespaceMatches()
    {
        var result = CodeQueryResolver.Expand("@Shared/Utils", SelfPath).ToList();
        result.Should().HaveCount(2);
        result[0].Should().Be("path:Shared/Utils nodeType:Code");
        result[1].Should().Be("namespace:Shared/Utils scope:subtree nodeType:Code");
    }

    [Fact]
    public void Expand_DoubleAtShorthand_AlsoYieldsBothForms()
    {
        // @@ is accepted historically for inline includes; the query-level resolver
        // treats it the same as @ so copy-pasted references just work.
        var result = CodeQueryResolver.Expand("@@Shared/Utils", SelfPath).ToList();
        result.Should().HaveCount(2);
    }

    [Fact]
    public void Expand_AtWithAlreadyQualifiedQuery_PassesThroughOnce()
    {
        var result = CodeQueryResolver.Expand("@namespace:Shared/Lib scope:subtree", SelfPath).ToList();
        result.Should().ContainSingle()
            .Which.Should().Be("namespace:Shared/Lib scope:subtree nodeType:Code");
    }

    [Fact]
    public void Expand_PreservesExistingNodeTypeFilter()
    {
        // Don't double-up the nodeType:Code filter when the author already wrote one.
        var result = CodeQueryResolver.Expand("namespace:$self/Source scope:subtree nodeType:Code", SelfPath).ToList();
        result.Should().ContainSingle()
            .Which.Should().Be($"namespace:{SelfPath}/Source scope:subtree nodeType:Code");
    }

    [Fact]
    public void ExpandAll_EmptyOrNull_FallsBackToDefaults()
    {
        var fromNull = CodeQueryResolver.ExpandAll(null, CodeQueryResolver.DefaultSources, SelfPath).ToList();
        var fromEmpty = CodeQueryResolver.ExpandAll(new List<string>(), CodeQueryResolver.DefaultSources, SelfPath).ToList();

        fromNull.Should().BeEquivalentTo(fromEmpty, System.Text.Json.JsonSerializerOptions.Default);
        fromNull.Should().ContainSingle()
            .Which.Should().Be($"namespace:{SelfPath}/Source scope:subtree nodeType:Code");
    }

    [Fact]
    public void ExpandAll_DefaultTests_ResolvesToTestSubfolder()
    {
        var queries = CodeQueryResolver.ExpandAll(null, CodeQueryResolver.DefaultTests, SelfPath).ToList();
        queries.Should().ContainSingle()
            .Which.Should().Be($"namespace:{SelfPath}/Test scope:subtree nodeType:Code");
    }

    [Fact]
    public void ExpandAll_MultipleEntries_EmitsInOrderAndKeepsDuplicates()
    {
        var raw = new[]
        {
            "namespace:Source scope:subtree",
            "@Shared/Utils",
            "namespace:Other/Lib scope:subtree"
        };
        var result = CodeQueryResolver.ExpandAll(raw, CodeQueryResolver.DefaultSources, SelfPath).ToList();
        result.Should().Equal(
            $"namespace:{SelfPath}/Source scope:subtree nodeType:Code",
            "path:Shared/Utils nodeType:Code",
            "namespace:Shared/Utils scope:subtree nodeType:Code",
            "namespace:Other/Lib scope:subtree nodeType:Code");
    }

    [Fact]
    public void ExpandAll_SkipsBlankEntries()
    {
        var raw = new[] { "namespace:Source scope:subtree", "", "   " };
        var result = CodeQueryResolver.ExpandAll(raw, CodeQueryResolver.DefaultSources, SelfPath).ToList();
        result.Should().ContainSingle();
    }

    [Fact]
    public void ParseName_NamedEntry_SplitsNameAndQuery()
    {
        var (name, query) = CodeQueryResolver.ParseName("shared=@Lib/Common");
        name.Should().Be("shared");
        query.Should().Be("@Lib/Common");
    }

    [Fact]
    public void ParseName_UnnamedEntry_ReturnsNullName()
    {
        var (name, query) = CodeQueryResolver.ParseName("namespace:Source scope:subtree");
        name.Should().BeNull();
        query.Should().Be("namespace:Source scope:subtree");
    }

    [Fact]
    public void ParseName_NameWithSpaceOrColon_IsNotAName()
    {
        // A "name" containing whitespace or query syntax means the '=' belongs to
        // the query body — don't split.
        CodeQueryResolver.ParseName("namespace:Source x=y").Name.Should().BeNull();
        CodeQueryResolver.ParseName("nodeType:Code=x").Name.Should().BeNull();
    }

    [Fact]
    public void Expand_NamedEntry_StripsNameBeforeExpansion()
    {
        // The compiler path must behave identically for named and unnamed entries.
        var named = CodeQueryResolver.Expand("mysrc=namespace:Source scope:subtree", SelfPath).ToList();
        var unnamed = CodeQueryResolver.Expand("namespace:Source scope:subtree", SelfPath).ToList();
        named.Should().Equal(unnamed);
    }

    [Fact]
    public void GroupAll_Defaults_YieldOneDefaultNamedGroup()
    {
        var groups = CodeQueryResolver.GroupAll(
            null, CodeQueryResolver.DefaultSources, SelfPath, CodeQueryResolver.DefaultSourceGroupName);

        var group = groups.Should().ContainSingle().Subject;
        group.Name.Should().Be("src");
        group.ExpandedQueries.Should().ContainSingle()
            .Which.Should().Be($"namespace:{SelfPath}/Source scope:subtree nodeType:Code");
        group.BaseNamespace.Should().Be($"{SelfPath}/Source");
    }

    [Fact]
    public void GroupAll_DefaultTests_UseTestGroupName()
    {
        var groups = CodeQueryResolver.GroupAll(
            null, CodeQueryResolver.DefaultTests, SelfPath, CodeQueryResolver.DefaultTestGroupName);

        var group = groups.Should().ContainSingle().Subject;
        group.Name.Should().Be("test");
        group.BaseNamespace.Should().Be($"{SelfPath}/Test");
    }

    [Fact]
    public void GroupAll_NamedAndUnnamedEntries_GroupInFirstAppearanceOrder()
    {
        var raw = new[]
        {
            "namespace:Source scope:subtree",        // → default group "src"
            "shared=@Lib/Common",                    // → "shared"
            "shared=namespace:Lib/Extra scope:subtree", // appended to "shared"
        };
        var groups = CodeQueryResolver.GroupAll(
            raw, CodeQueryResolver.DefaultSources, SelfPath, CodeQueryResolver.DefaultSourceGroupName);

        groups.Should().HaveCount(2);
        groups[0].Name.Should().Be("src");
        groups[1].Name.Should().Be("shared");
        groups[1].RawQueries.Should().Equal("@Lib/Common", "namespace:Lib/Extra scope:subtree");
        // Mixed namespace roots (Lib/Common vs Lib/Extra) → no common base.
        groups[1].BaseNamespace.Should().BeNull();
    }

    [Fact]
    public void GroupAll_AtShorthandGroup_BaseIsTheSharedRoot()
    {
        var raw = new[] { "shared=@Lib/Common" };
        var groups = CodeQueryResolver.GroupAll(
            raw, CodeQueryResolver.DefaultSources, SelfPath, CodeQueryResolver.DefaultSourceGroupName);

        // @ shorthand expands to path: + namespace: queries over the same root.
        groups.Should().ContainSingle().Which.BaseNamespace.Should().Be("Lib/Common");
    }

    [Fact]
    public void Matches_SubtreeNamespace_MatchesNestedPaths()
    {
        var queries = CodeQueryResolver.Expand("namespace:Test scope:subtree", SelfPath).ToList();
        CodeQueryResolver.Matches($"{SelfPath}/Test/Deep/File", queries).Should().BeTrue();
        CodeQueryResolver.Matches($"{SelfPath}/Source/File", queries).Should().BeFalse();
    }

    [Fact]
    public void Matches_NamespaceWithoutSubtree_MatchesDirectChildrenOnly()
    {
        var queries = new[] { $"namespace:{SelfPath}/Test nodeType:Code" };
        CodeQueryResolver.Matches($"{SelfPath}/Test/File", queries).Should().BeTrue();
        CodeQueryResolver.Matches($"{SelfPath}/Test/Deep/File", queries).Should().BeFalse();
    }

    [Fact]
    public void Matches_ExactPath_MatchesThatPathOnly()
    {
        var queries = new[] { "path:Lib/Common nodeType:Code" };
        CodeQueryResolver.Matches("Lib/Common", queries).Should().BeTrue();
        CodeQueryResolver.Matches("Lib/Common/Sub", queries).Should().BeFalse();
    }
}
