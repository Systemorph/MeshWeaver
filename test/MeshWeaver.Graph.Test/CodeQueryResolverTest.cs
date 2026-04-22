using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
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

        fromNull.Should().BeEquivalentTo(fromEmpty);
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
}
