using System.Collections.Immutable;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.BusinessRules.Test;

/// <summary>
/// FX conversion expressed as a BusinessRules scope — the canonical example of what a
/// <c>Scope</c> NodeType holds as its content: an <c>IScope&lt;TIdentity,TState&gt;</c> whose
/// computed properties ARE the business rule. Here the rule is a currency cross-rate.
///
/// <para>The same source, dropped into a <c>{NodeType}/Source/*.cs</c> file with a
/// <c>// NodeType: Scope</c> header, is compiled at runtime by the NodeType compiler (which runs
/// the scope generator only for scope-bearing units). This build-time test pins the rule itself.</para>
/// </summary>
public class FxConversionScopeTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .WithServices(services => services.AddBusinessRules(GetType().Assembly));

    [Fact]
    public void Converts_via_cross_rate()
    {
        // Rates as "1 unit of currency = X base units" (base = USD).
        var rates = new FxRates(ImmutableDictionary<string, decimal>.Empty
            .Add("USD", 1.00m)
            .Add("EUR", 1.08m)
            .Add("CHF", 1.12m));

        var registry = GetHost().ServiceProvider.CreateScopeRegistry<FxRates>(rates);

        // 1 EUR = 1.08 USD, 1 CHF = 1.12 USD  ⇒  EUR/CHF cross rate = 1.08 / 1.12.
        var eurToChf = registry.GetScope<ICurrencyConversion>(new FxTrade("EUR", "CHF", 100m));
        eurToChf.Rate.Should().Be(1.08m / 1.12m);
        eurToChf.Converted.Should().Be(100m * (1.08m / 1.12m));

        // Same currency converts 1:1.
        var usdToUsd = registry.GetScope<ICurrencyConversion>(new FxTrade("USD", "USD", 250m));
        usdToUsd.Rate.Should().Be(1m);
        usdToUsd.Converted.Should().Be(250m);
    }
}

/// <summary>Per-currency exchange rates to a common base currency.</summary>
public record FxRates(ImmutableDictionary<string, decimal> ToBase);

/// <summary>An amount to convert from one currency to another.</summary>
public record FxTrade(string From, string To, decimal Amount);

/// <summary>
/// The FX business rule: cross rate = (from→base) / (to→base); converted = amount × rate.
/// Scope instances are cached per <see cref="FxTrade"/> identity by the registry.
/// </summary>
public interface ICurrencyConversion : IScope<FxTrade, FxRates>
{
    decimal FromRate => GetStorage().ToBase[Identity.From];
    decimal ToRate => GetStorage().ToBase[Identity.To];
    decimal Rate => FromRate / ToRate;
    decimal Converted => Identity.Amount * Rate;
}
