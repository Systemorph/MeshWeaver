#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Unit tests for pure static helper methods in AgentChatClient:
/// OrderAgentsForCreation and FindCyclicDelegations.
/// </summary>
public class AgentChatClientUnitTest
{
    #region OrderAgentsForCreation

    [Fact]
    public void OrderAgentsForCreation_MixedTypes_ReturnsNonDelegatingThenDelegatingThenDefault()
    {
        var configs = new List<AgentConfiguration>
        {
            new() { Id = "Default", IsDefault = true },
            new() { Id = "Delegating", Delegations = new List<AgentDelegation> { new() { AgentPath = "Other" } } },
            new() { Id = "Plain" },
        };

        var result = AgentChatClient.OrderAgentsForCreation(configs).ToList();

        result.Should().HaveCount(3);
        result[0].Id.Should().Be("Plain", "non-delegating agents come first");
        result[1].Id.Should().Be("Delegating", "delegating agents come second");
        result[2].Id.Should().Be("Default", "default agent comes last");
    }

    [Fact]
    public void OrderAgentsForCreation_EmptyList_ReturnsEmpty()
    {
        var result = AgentChatClient.OrderAgentsForCreation(new List<AgentConfiguration>()).ToList();

        result.Should().BeEmpty();
    }

    [Fact]
    public void OrderAgentsForCreation_OnlyDefaultAgent_ReturnsSingleItem()
    {
        var configs = new List<AgentConfiguration>
        {
            new() { Id = "Default", IsDefault = true }
        };

        var result = AgentChatClient.OrderAgentsForCreation(configs).ToList();

        result.Should().ContainSingle().Which.Id.Should().Be("Default");
    }

    [Fact]
    public void OrderAgentsForCreation_AllNonDelegating_ReturnsAll()
    {
        var configs = new List<AgentConfiguration>
        {
            new() { Id = "A" },
            new() { Id = "B" },
            new() { Id = "C" },
        };

        var result = AgentChatClient.OrderAgentsForCreation(configs).ToList();

        result.Should().HaveCount(3);
        result.Select(a => a.Id).Should().Equal("A", "B", "C");
    }

    [Fact]
    public void OrderAgentsForCreation_MultipleDelegatingAndDefault_DelegatingBeforeDefault()
    {
        var configs = new List<AgentConfiguration>
        {
            new() { Id = "Default", IsDefault = true },
            new() { Id = "D1", Delegations = new List<AgentDelegation> { new() { AgentPath = "X" } } },
            new() { Id = "D2", Delegations = new List<AgentDelegation> { new() { AgentPath = "Y" } } },
        };

        var result = AgentChatClient.OrderAgentsForCreation(configs).ToList();

        result.Should().HaveCount(3);
        result[0].Id.Should().Be("D1");
        result[1].Id.Should().Be("D2");
        result[2].Id.Should().Be("Default");
    }

    [Fact]
    public void OrderAgentsForCreation_DefaultWithDelegations_TreatedAsDefault()
    {
        // An agent that is both default AND has delegations should appear in the default bucket
        var configs = new List<AgentConfiguration>
        {
            new() { Id = "Plain" },
            new() { Id = "DefaultDelegator", IsDefault = true, Delegations = new List<AgentDelegation> { new() { AgentPath = "Plain" } } },
        };

        var result = AgentChatClient.OrderAgentsForCreation(configs).ToList();

        result.Should().HaveCount(2);
        result[0].Id.Should().Be("Plain", "non-delegating comes first");
        result[1].Id.Should().Be("DefaultDelegator", "default agent comes last even with delegations");
    }

    #endregion

    #region FindCyclicDelegations

    [Fact]
    public void FindCyclicDelegations_NoDelegations_ReturnsEmpty()
    {
        var configs = new List<AgentConfiguration>
        {
            new() { Id = "A" },
            new() { Id = "B" },
        };

        var result = AgentChatClient.FindCyclicDelegations(configs).ToList();

        result.Should().BeEmpty();
    }

    [Fact]
    public void FindCyclicDelegations_OneWayDelegation_ReturnsEmpty()
    {
        var configs = new List<AgentConfiguration>
        {
            new() { Id = "A", Delegations = new List<AgentDelegation> { new() { AgentPath = "ns/B" } } },
            new() { Id = "B" },
        };

        var result = AgentChatClient.FindCyclicDelegations(configs).ToList();

        result.Should().BeEmpty();
    }

    [Fact]
    public void FindCyclicDelegations_MutualDelegation_ReturnsBoth()
    {
        var configs = new List<AgentConfiguration>
        {
            new() { Id = "A", Delegations = new List<AgentDelegation> { new() { AgentPath = "ns/B" } } },
            new() { Id = "B", Delegations = new List<AgentDelegation> { new() { AgentPath = "ns/A" } } },
        };

        var result = AgentChatClient.FindCyclicDelegations(configs).ToList();

        result.Should().HaveCount(2);
        result.Select(a => a.Id).Should().BeEquivalentTo("A", "B");
    }

    [Fact]
    public void FindCyclicDelegations_Chain_ReturnsEmpty()
    {
        var configs = new List<AgentConfiguration>
        {
            new() { Id = "A", Delegations = new List<AgentDelegation> { new() { AgentPath = "ns/B" } } },
            new() { Id = "B", Delegations = new List<AgentDelegation> { new() { AgentPath = "ns/C" } } },
            new() { Id = "C" },
        };

        var result = AgentChatClient.FindCyclicDelegations(configs).ToList();

        result.Should().BeEmpty();
    }

    [Fact]
    public void FindCyclicDelegations_MixedCyclicAndNonCyclic_ReturnsOnlyCyclic()
    {
        var configs = new List<AgentConfiguration>
        {
            new() { Id = "A", Delegations = new List<AgentDelegation> { new() { AgentPath = "ns/B" } } },
            new() { Id = "B", Delegations = new List<AgentDelegation> { new() { AgentPath = "ns/A" } } },
            new() { Id = "C", Delegations = new List<AgentDelegation> { new() { AgentPath = "ns/D" } } },
            new() { Id = "D" },
        };

        var result = AgentChatClient.FindCyclicDelegations(configs).ToList();

        result.Should().HaveCount(2);
        result.Select(a => a.Id).Should().BeEquivalentTo("A", "B");
    }

    [Fact]
    public void FindCyclicDelegations_EmptyInput_ReturnsEmpty()
    {
        var result = AgentChatClient.FindCyclicDelegations(new List<AgentConfiguration>()).ToList();

        result.Should().BeEmpty();
    }

    [Fact]
    public void FindCyclicDelegations_DelegationsWithNoMatchingTarget_ReturnsEmpty()
    {
        var configs = new List<AgentConfiguration>
        {
            new() { Id = "A", Delegations = new List<AgentDelegation> { new() { AgentPath = "ns/NonExistent" } } },
        };

        var result = AgentChatClient.FindCyclicDelegations(configs).ToList();

        result.Should().BeEmpty();
    }

    #endregion
}
