using System.Reflection;
using System.Security.Principal;
using FluentAssertions;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using MeshWeaver.Reflection;
using Xunit;
using Xunit.Abstractions;

namespace MeshWeaver.BusinessRules.Test;

public class ScopesTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
    {
        return base.ConfigureHost(configuration)
            .AddBusinessRules(GetType().Assembly);
    }

    [Fact]
    public void RandomNumberIsCached()
    {
        var registry = GetHost().GetScopeRegistry<IRandomScope>(null);
        var randomScope = registry.GetScope<IRandomScope>(Guid.NewGuid());

        var randomNumber = randomScope.RandomNumber;
        randomNumber.Should().NotBe(0);

        // once the property is evaluated, it is cached ==> we should get the same number.
        randomScope.RandomNumber.Should().Be(randomNumber);
    }
}


public interface IRandomScope : IScope<Guid, object>
{
    private static Random Random = new Random();
    public double RandomNumber => Random.NextDouble();
}


//public class RansomScopeProxyManual
//    : ScopeBase<IRandomScope, Guid, object>, IRandomScope
//{
//    private static readonly MethodInfo __randomNumberGetter = typeof(IRandomScope).GetProperty(nameof(IRandomScope.RandomNumber)).GetMethod;
//    private readonly Lazy<double> __randomNumber;

//    public RansomScopeProxyManual(Guid identity, ScopeRegistry<object> state) : base(identity, state)
//    {
//        __randomNumber = new(() => Evaluate<double>(__randomNumberGetter));
//    }

//    double IRandomScope.RandomNumber => __randomNumber.Value;
//}
