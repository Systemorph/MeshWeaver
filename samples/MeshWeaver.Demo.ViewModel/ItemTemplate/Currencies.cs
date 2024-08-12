using System.Reactive.Linq;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Layout;

namespace MeshWeaver.Demo.ViewModel.ItemTemplate;

public static class Currencies
{
    private static readonly List<(string SystemName, string DisplayName)> currencyNames =
        [
            ("CHF", "Swiss Franc"),
            ("EUR", "Euro"),
            ("USD", "US Dollar"),
            ("GBP", "British Pound"),
            ("CNY", "Chinese Yuan"),
        ];

    public static object CurrenciesFilter(LayoutAreaHost area, RenderingContext context)
        => Controls
            .Stack
            .WithVerticalGap(16)
            .WithView(
                (a, _) =>
                    Template.Bind(
                        new CurrenciesFilter(currencyNames.Select(c => new CurrenciesFilterItem(c.SystemName, c.DisplayName, false)).ToList()),
                        nameof(CurrenciesFilter),
                        cf => Template.ItemTemplate(cf.Data, c => Controls.CheckBox(c.Label, c.Selected))
                    )
            )
            .WithView((a, _) => a
                .GetDataStream<CurrenciesFilter>(nameof(CurrenciesFilter))
                .Select(x => ShowSelectedCurrencies(x.Data)), nameof(ShowSelectedCurrencies)
            );

    private static object ShowSelectedCurrencies(IEnumerable<CurrenciesFilterItem> currencyData)
        => Controls.Html($"Currencies: {string.Join(",", currencyData.Where(x => x.Selected).Select(x => x.Id.ToString()))}");
}

internal record CurrenciesFilter(List<CurrenciesFilterItem> Data);

public record CurrenciesFilterItem(object Id, string Label, bool Selected);
