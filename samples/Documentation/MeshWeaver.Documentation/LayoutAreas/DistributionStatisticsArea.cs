using System.Diagnostics;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Domain;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

namespace MeshWeaver.Documentation.LayoutAreas;

/// <summary>
/// The distribution statistics is used as an example of how to build
/// interactive dialogs using reactive principles.
/// </summary>
public static class DistributionStatisticsArea
{
    #region Domain
    /// <summary>
    /// Distribution base class
    /// </summary>
    public abstract record Distribution;

    /// <summary>
    /// Pareto distribution <see ref="https://en.wikipedia.org/wiki/Pareto_distribution"/>
    /// </summary>
    /// <param name="Alpha"></param>
    /// <param name="X0"></param>
    public record Pareto(double Alpha = 2, double X0 = 1) : Distribution;

    /// <summary>
    /// LogNormal distribution <see ref="https://en.wikipedia.org/wiki/Log-normal_distribution"/>
    /// </summary>
    /// <param name="Mu"></param>
    /// <param name="Sigma"></param>
    public record LogNormal(double Mu = 1, double Sigma = 1) : Distribution;

    /// <summary>
    /// Basic input section for the simulation
    /// </summary>
    public record BasicInput
    {
        /// <summary>
        /// Number of samples used in the simulation
        /// </summary>
        public int Samples { get; init; } = 1000;

        /// <summary>
        /// The choice of the distribution type
        /// </summary>
        [Dimension<string>(Options = nameof(DistributionTypes))]
        public string DistributionType { get; init; } = "Pareto";

    }

    /// <summary>
    /// List of all distributions
    /// </summary>
    public static readonly Dictionary<string, Distribution> Distributions = new()
    {
        [nameof(Pareto)] = new Pareto(),
        [nameof(LogNormal)] = new LogNormal()
    };

    /// <summary>
    /// All distribution types
    /// </summary>
    public static readonly Option<string>[] DistributionTypes =
        Distributions.Keys.Select(d => new Option<string>(d, d)).ToArray();

    #endregion
    #region Actuarial Methodology



    /// <summary>
    /// Trigger the simulation
    /// </summary>
    /// <param name="basicInput">Basic Inputs for the simulation</param>
    /// <param name="distribution">Distribution to simulate</param>
    /// <returns></returns>
    private static (double[] Samples, TimeSpan Time) Simulate(BasicInput basicInput, Distribution distribution)
    {
        var rng = new Random();
        var sw = Stopwatch.StartNew();
        var samples = distribution switch
        {
            Pareto pareto => Enumerable.Range(0, basicInput.Samples)
                .Select(_ => pareto.X0 / Math.Pow(1 - rng.NextDouble(), 1.0 / pareto.Alpha)).ToArray(),
            LogNormal logNormal => Enumerable.Range(0, basicInput.Samples)
                .Select(_ => rng.SampleLogNormal(logNormal.Mu, logNormal.Sigma)).ToArray(),
            _ => throw new NotSupportedException($"Unknown distribution type {distribution.GetType().Name}")
        };

        return (samples, sw.Elapsed);
    }

    /// <summary>
    /// This method generates a sample from the standard normal distribution using box muller.
    /// </summary>
    /// <param name="rng"></param>
    /// <returns></returns>
    private static double SampleStandardNormal(this Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
    /// <summary>
    /// This method samples log normal.
    /// </summary>
    /// <param name="rng"></param>
    /// <param name="mu"></param>
    /// <param name="sigma"></param>
    /// <returns></returns>
    private static double SampleLogNormal(this Random rng, double mu, double sigma)
    {
        var z = rng.SampleStandardNormal();
        return Math.Exp(mu + sigma * z);
    }

    /// <summary>
    /// This method calculates the mean and variance of a sample.
    /// </summary>
    /// <param name="tuple"></param>
    /// <returns></returns>
    public static MarkdownControl Statistics(this (double[] Samples, TimeSpan Time) tuple)
    {
        var samples = tuple.Samples;
        var mean = samples.Average();
        var variance = samples.Select(x => Math.Pow(x - mean, 2)).Average();
        return Controls.Markdown(
            @$"### Key Statistics
| Mean | Variance |
|------|----------|
| {mean:F1} | {variance:F1} |

Execution Time: {tuple.Time}" );
    }

    #endregion

    #region layout area

    /// <summary>
    /// A distribution statistics dialog to simulate distributions and show statistics.
    /// </summary>
    /// <param name="host"></param>
    /// <param name="context"></param>
    /// <returns></returns>
    public static UiControl? DistributionStatistics(LayoutAreaHost host, RenderingContext context)
    {
        host.UpdateData(nameof(DistributionTypes), DistributionTypes);
        var subject = new Subject<(double[] Samples, TimeSpan Time)>();


        host.RegisterForDisposal(host.GetDataStream<BasicInput>(nameof(BasicInput))
            .Select(x => x!.DistributionType)
            .DistinctUntilChanged()
            .Subscribe(t => host.UpdateData(nameof(Distribution), Distributions[t])));

         return Controls.Stack
            .WithView(host.Edit(new BasicInput(), nameof(BasicInput)), nameof(BasicInput))
            .WithView(host.GetDataStream<Distribution>(nameof(Distribution)).Select(x => x!.GetType())
                .DistinctUntilChanged()
                .Select(t => host.Edit(t, nameof(Distribution))))
            .WithView(Controls.Button("Run Simulation")
                .WithClickAction(
                    async _ =>
                    {
                        var input = await host.Stream.GetDataAsync<BasicInput>(nameof(BasicInput));
                        var distribution = await host.Stream.GetDataAsync<Distribution>(nameof(Distribution));
                        subject.OnNext(Simulate(input, distribution));
                    }))
            .WithView(subject.Select(x => x.Statistics()).StartWith(Controls.Markdown("### Click to run simulation")));
    }


    /// <summary>
    /// Adds the distribution statistics to the layout.
    /// </summary>
    /// <param name="layout"></param>
    /// <returns></returns>
    public static LayoutDefinition AddDistributionStatistics(this LayoutDefinition layout)
        => layout.WithView(nameof(DistributionStatistics), DistributionStatistics);

    #endregion
}
