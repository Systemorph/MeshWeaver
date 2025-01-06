using System;
using FluentAssertions;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.BusinessRules.Test;

public class ScopesTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .WithServices(services => services.AddBusinessRules(GetType().Assembly))
            ;
    }

    [Fact]
    public void RandomNumberIsCached()
    {
        var registry = GetHost().ServiceProvider.CreateScopeRegistry<object>(null);
        var randomScope = registry.GetScope<IRandomScope>(Guid.NewGuid());

        var randomNumber = randomScope.RandomNumber;
        randomNumber.Should().NotBe(0);

        // once the property is evaluated, it is cached ==> we should get the same number.
        randomScope.RandomNumber.Should().Be(randomNumber);

        var otherScope = randomScope.GetScope<IRandomScope>(Guid.NewGuid());
        otherScope.RandomNumber.Should().NotBe(randomNumber);

        var originalScope = otherScope.GetScope<IRandomScope>(randomScope.Identity);
        originalScope.RandomNumber.Should().Be(randomNumber);
    }
}


public interface IRandomScope : IScope<Guid, object>
{
    private static Random Random = new Random();
    public double RandomNumber => Random.NextDouble();
}
