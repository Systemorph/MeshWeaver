using OpenSmc.Charting.Models.Options;
using OpenSmc.Charting.Models.Options.Animation;
using OpenSmc.Charting.Models.Options.Scales;

namespace OpenSmc.Charting.Builders.OptionsBuilders;

public abstract record OptionsBuilderBase<TOptionsBuilder>
    where TOptionsBuilder : OptionsBuilderBase<TOptionsBuilder>
{
    internal ChartOptions Options = new();
    public ChartOptions Build() => Options;

    public TOptionsBuilder WithoutAnimation()
        => (TOptionsBuilder)(this with { Options = Options with { Animation = (Options.Animation ?? new Animation()) with { Duration = 0 } } });

    public TOptionsBuilder WithIndexAxis(string indexAxis)
        => (TOptionsBuilder)(this with { Options = Options with { IndexAxis = indexAxis } });

    public TOptionsBuilder WithScales(Dictionary<string, Scale> scales)
        => (TOptionsBuilder)(this with { Options = Options with { Scales = scales } });

    public TOptionsBuilder ShortenXAxisNumbers() => ShortenAxisNumbers("x");
    public TOptionsBuilder ShortenYAxisNumbers() => ShortenAxisNumbers("y");

    public TOptionsBuilder Responsive(bool responsive = true) => (TOptionsBuilder)(this with { Options = Options with { Responsive = responsive } });

    public TOptionsBuilder Stacked()
    {
        InitializeScale<Scale>("x");
        Options.Scales["x"] = Options.Scales["x"] with { Stacked = true };

        InitializeScale<Scale>("y");
        Options.Scales["y"] = Options.Scales["y"] with { Stacked = true };

        return (TOptionsBuilder)this;
    }

    public TOptionsBuilder Grace<TScale>(string axis, object grace)
    where TScale : CartesianLinearScale
    {
        InitializeScale<CartesianLinearScale>(axis);
        Options.Scales[axis] = (Options.Scales[axis] as CartesianLinearScale ?? new CartesianLinearScale()) with { Grace = grace };
        return (TOptionsBuilder)this;
    }

    private void InitializeScale<TScale>(string key)
        where TScale : Scale, new()
    {
        Options = Options with { Scales = Options.Scales ?? new Dictionary<string, Scale>() };

        if (!Options.Scales.TryGetValue(key, out var scale) || scale is not TScale)
            scale = new TScale();

        Options.Scales[key] = scale;
    }

    public TOptionsBuilder Stacked(params string[] axes)
    {
        foreach (var axis in axes)
        {
            InitializeScale<Scale>(axis);
            Options.Scales[axis] = Options.Scales[axis] with { Stacked = true };
        }

        return (TOptionsBuilder)this;
    }

    public TOptionsBuilder HideAxis(string axis)
    {
        InitializeScale<Scale>(axis);
        Options.Scales[axis] = Options.Scales[axis] with { Display = false };
        
        return (TOptionsBuilder)this;
    }
    
    public TOptionsBuilder HideGrid(string axis)
    {
        InitializeScale<Scale>(axis);
        Options.Scales[axis] = Options.Scales[axis] with { Grid = (Options.Scales[axis].Grid ?? new Grid()) with { Display = false } };

        return (TOptionsBuilder)this;
    }

    public TOptionsBuilder SuggestedMax(string axis, int max)
    {
        InitializeScale<Scale>(axis);
        Options.Scales[axis] = Options.Scales[axis] with { SuggestedMax = max };

        return (TOptionsBuilder)this;
    }

    public TOptionsBuilder WithXAxisMin(int minimum) => WithAxisMin("x", minimum);
    public TOptionsBuilder WithYAxisMin(int minimum) => WithAxisMin("y", minimum);
    public TOptionsBuilder WithXAxisMin(double minimum) => WithAxisMin("x", minimum);
    public TOptionsBuilder WithYAxisMin(double minimum) => WithAxisMin("y", minimum);

    public TOptionsBuilder WithXAxisMax(int maximum) => WithAxisMax("x", maximum);
    public TOptionsBuilder WithYAxisMax(int maximum) => WithAxisMax("y", maximum);
    public TOptionsBuilder WithXAxisMax(double maximum) => WithAxisMax("x", maximum);
    public TOptionsBuilder WithYAxisMax(double maximum) => WithAxisMax("y", maximum);


    public TOptionsBuilder WithXAxisStep(int step) => WithAxisStep(step, "x");
    public TOptionsBuilder WithYAxisStep(int step) => WithAxisStep(step, "y");
    public TOptionsBuilder WithXAxisStep(double step) => WithAxisStep(step, "x");
    public TOptionsBuilder WithYAxisStep(double step) => WithAxisStep(step, "y");

    public TOptionsBuilder WithScales(params (string, Scale)[] scales)
        => (TOptionsBuilder)(this with { Options = Options with { Scales = scales.ToDictionary(tuple => tuple.Item1, tuple => tuple.Item2) } });

    public TOptionsBuilder WithPlugins(Func<Models.Options.Plugins, Models.Options.Plugins> pluginsModifier)
        => (TOptionsBuilder)(this with { Options = Options with { Plugins = pluginsModifier(Options.Plugins) } });

    public TOptionsBuilder WithMaintainAspectRatio(bool maintainAspectRatio)
        => (TOptionsBuilder)(this with { Options = Options with { MaintainAspectRatio = maintainAspectRatio } });

    public TOptionsBuilder WithAspectRatio(double? maintainAspectRatio)
        => (TOptionsBuilder)(this with { Options = Options with { AspectRatio = maintainAspectRatio, MaintainAspectRatio = true } });

    public TOptionsBuilder WithLayout(Models.Layout.Layout layout)
        => (TOptionsBuilder)(this with { Options = Options with { Layout = layout } });

    public TOptionsBuilder WithLayout(Func<Models.Layout.Layout, Models.Layout.Layout> layoutModifier)
        => (TOptionsBuilder)(this with { Options = Options with { Layout = layoutModifier(Options.Layout ?? new Models.Layout.Layout()) } });

    private TOptionsBuilder WithAxisMin(string axis, double minimum)
    {
        InitializeScale<Scale>(axis);
        Options.Scales[axis] = Options.Scales[axis] with { Min = minimum };

        return (TOptionsBuilder)this;
    }

    private TOptionsBuilder WithAxisMax(string axis, double maximum)
    {
        InitializeScale<Scale>(axis);
        Options.Scales[axis] = Options.Scales[axis] with { Max = maximum };

        return (TOptionsBuilder)this;
    }

    private TOptionsBuilder WithAxisStep(double step, string axis)
    {
        InitializeScale<Linear>(axis);
        Options.Scales[axis] = Options.Scales[axis] with { Ticks = (Options.Scales[axis].Ticks as CartesianLinearTick ?? new CartesianLinearTick()) with { StepSize = step } };

        return (TOptionsBuilder)this;
    }

    public TOptionsBuilder ShortenAxisNumbers(string axis)
    {
        InitializeScale<Scale>(axis);
        Options.Scales[axis] = Options.Scales[axis] with
                               {
                                   Ticks = (Options.Scales[axis].Ticks as CartesianLinearTick ?? new CartesianLinearTick()) with
                                           {
                                               Format = new
                                                        {
                                                            notation = "compact",
                                                            minimumFractionDigits = 0,
                                                            maximumFractionDigits = 2
                                                        }
                                           }
                               };

        return (TOptionsBuilder)this;
    }
}
