namespace OpenSmc.Charting.Builders.OptionsBuilders;

public abstract record ArrayOptionsBuilder<TOptionsBuilder> : OptionsBuilderBase<TOptionsBuilder>
    where TOptionsBuilder : ArrayOptionsBuilder<TOptionsBuilder>;