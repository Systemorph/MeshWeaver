using MeshWeaver.Charting.Enums;
using MeshWeaver.Charting.Helpers;
using MeshWeaver.Charting.Models.Options;

namespace MeshWeaver.Charting.Models
{
    public record Chart
    {
        public Chart(IReadOnlyCollection<DataSet> dataSets, ChartType type)
        {
            Type = type;
            Data = Data.WithDataSets(dataSets);
            Options = GetAutoLegendOptions();
        }

        /// <summary>
        /// Chart type e.g. bar, line, etc
        /// </summary>
        public ChartType Type { get; init; }

        /// <summary>
        /// Chart data
        /// </summary>
        public ChartData Data { get; init; } = new();

        /// <summary>
        /// Chart options configuration
        /// </summary>
        public ChartOptions Options { get; init; } = new();

        protected bool AutoLabels { get; init; }

        public Chart WithDataSet<TDataSet2>(TDataSet2 dataSet) where TDataSet2 : DataSet
            => (this with { Data = Data.WithDataSets(dataSet), })
                .WithAutoUpdatedLabels()
                .WithAutoLegend();

        public virtual Chart WithLabels(params string[] labels) =>
            WithLabels(labels.AsReadOnly());

        public virtual Chart WithLabels(IReadOnlyCollection<string> labels) =>
            this with { AutoLabels = false, Data = Data.WithLabels(labels), };


        public Chart WithAutoLabels() => this with { AutoLabels = true, };

        protected IReadOnlyCollection<string> GetUpdatedLabels()
        {
            if (AutoLabels && Data.DataSets.Count > 0)
            {
                var maxLen = Data.DataSets.Select(ds => ds.Data?.Count() ?? 0).DefaultIfEmpty(1).Max();

                return Enumerable.Range(1, maxLen).Select(i => i.ToString()).ToArray();
            }
            return Data.Labels;
        }

        private Chart WithAutoUpdatedLabels() => this with { Data = Data with { Labels = GetUpdatedLabels(), } };

        private ChartOptions GetAutoLegendOptions()
        {
            if (Data.DataSets.Count > 1 && Data.DataSets.Any(item => item.HasLabel()))
                return Options.WithPlugins(p => p.WithLegend());
            return Options;
        }

        private Chart WithAutoLegend() => this with { Options = GetAutoLegendOptions(), };

        public Chart WithLegend(Func<Legend, Legend> builder = null)
            => WithOptions(o => o.WithPlugins(p => p.WithLegend(builder)));

        public Chart WithTitle(string text, Func<Title, Title> titleModifier = null)
            => WithOptions(o => o.WithPlugins(p =>
            {
                p.Title = new Title { Text = text, Display = true, Font = ChartFonts.MainTitle, };
                if (titleModifier != null)
                    return p with { Title = titleModifier(p.Title), };
                return p;
            }));

        public Chart WithSubTitle(string text, Func<Title, Title> titleModifier = null)
            => WithOptions(o => o.WithPlugins(p =>
            {
                p.Subtitle = new Title { Text = text, Display = true, Font = ChartFonts.SubTitle, };
                if (titleModifier != null)
                    return p with { Subtitle = titleModifier(p.Subtitle), };
                return p;
            }));

        public Chart Stacked() => WithOptions(o => o.Stacked());

        public Chart WithColorPalette(string[] palette)
        {
            ChartHelpers.CheckPalette(palette);
            return WithOptions(o => o.WithPlugins(p => p with { ColorSchemes = new ColorSchemes { Scheme = palette }, }));
        }

        public Chart WithColorPalette(Palettes palette)
            => WithOptions(o => o.WithPlugins(p => p with { ColorSchemes = new ColorSchemes { Scheme = palette, }, }));

        public Chart WithOptions(Func<ChartOptions, ChartOptions> func) => this with { Options = func(Options), };

        public Chart WithDataLabels(Func<DataLabels, DataLabels> func = null) =>
            WithOptions(o => o.WithPlugins(p => p.WithDataLabels(func)));
    }
}
