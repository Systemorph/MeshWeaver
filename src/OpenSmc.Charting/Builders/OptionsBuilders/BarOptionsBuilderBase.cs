namespace OpenSmc.Charting.Builders.OptionsBuilders;

public abstract record BarOptionsBuilderBase<TBuilder> : ArrayOptionsBuilder<TBuilder>
    where TBuilder : BarOptionsBuilderBase<TBuilder>;