using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace OpenSmc.Scopes.Test;

public class SingleScopeTest : ScopesTestBase
{
    public SingleScopeTest(ITestOutputHelper toh)
        : base(toh)
    {
    }

    [Fact]
    public void Calculation()
    {
        var scopeBuilder = ScopeFactory.ForSingleton().WithContext("context1");
        var mainScope = scopeBuilder.ToScope<IMainScope>();
        mainScope.TotalValue.Should().Be(Enumerable.Range(0, IDummySimulationParams.DefaultNSimulations).Sum(i => IDummySimulationParams.DefaultCoefficient * i));

        mainScope.IncrementCoefficient();
        mainScope.TotalValue.Should().Be(Enumerable.Range(0, IDummySimulationParams.DefaultNSimulations).Sum(i => (IDummySimulationParams.DefaultCoefficient + 1) * i));
    }

    [Theory]
    [InlineData("ctx1")]
    [InlineData(null)]
    public void DifferentContext(string context)
    {
        var scopeBuilder = ScopeFactory.ForSingleton().WithContext(context);
        var mainScope = scopeBuilder.ToScope<IMainScope>();

        mainScope.IncrementCoefficient();

        var paramsScope = mainScope.GetScope<IDummySimulationParams>(o => o.WithContext(context));
        paramsScope.Coefficient.Should().Be(IDummySimulationParams.DefaultCoefficient + 1);

        mainScope.GetScope<IDummySimulationParams>().NSimulations += 1;

        paramsScope.NSimulations.Should().Be(IDummySimulationParams.DefaultNSimulations + 1);
    }
}