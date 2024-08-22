using MeshWeaver.Charting.Models.Options;

namespace MeshWeaver.Charting.Builders.OptionsBuilders;

public abstract record OptionsBuilderBase<TOptionsBuilder>
    where TOptionsBuilder : OptionsBuilderBase<TOptionsBuilder>
{
    internal ChartOptions Options = new();
    public ChartOptions Build() => Options;

}
