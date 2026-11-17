using System.Reactive.Linq;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// <see cref="NodeTypeAccessRuleSet.ExclusiveReadNodeTypes"/> names exactly the node types whose GOVERNING
/// Read rule declares <see cref="INodeTypeExclusiveRead"/> — the types a query that filters in its store must
/// never list through a grant (Doc/Architecture/AccessControl → "Rule read predicates").
///
/// <para>Negative controls: a non-exclusive rule, an exclusive rule that does not decide Read, and an
/// exclusive rule a later registration replaced each leave their type out.</para>
/// </summary>
public class ExclusiveReadNodeTypesTest
{
    private class Rule(string nodeType, params NodeOperation[] operations) : INodeTypeAccessRule
    {
        public string NodeType => nodeType;
        public IReadOnlyCollection<NodeOperation> SupportedOperations => operations;
        public IObservable<bool> HasAccess(NodeValidationContext context, string? userId) => Observable.Return(false);
    }

    private sealed class ExclusiveRule(string nodeType, params NodeOperation[] operations)
        : Rule(nodeType, operations), INodeTypeExclusiveRead;

    [Fact]
    public void OnlyAGoverningExclusiveReadRule_MakesItsTypeExclusive()
    {
        var set = new NodeTypeAccessRuleSet(
        [
            new ExclusiveRule("Feed", NodeOperation.Read, NodeOperation.Create),
            new ExclusiveRule("AllOps"),                                  // empty = every operation
            new Rule("Widening", NodeOperation.Read),                     // control: not exclusive
            new ExclusiveRule("WriteOnly", NodeOperation.Update),         // control: does not decide Read
            new ExclusiveRule("Replaced", NodeOperation.Read),
            new Rule("Replaced", NodeOperation.Read),                     // control: a later registration governs
        ]);

        set.ExclusiveReadNodeTypes().Should().Equal(["AllOps", "Feed"]);
    }

    [Fact]
    public void ARuleSetWithoutExclusiveRules_NamesNone()
        => new NodeTypeAccessRuleSet([new Rule("A", NodeOperation.Read)]).ExclusiveReadNodeTypes().Should().BeEmpty();
}
