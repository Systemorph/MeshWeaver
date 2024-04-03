namespace OpenSmc.Charting.Models.Options.Scales
{
    public record TimeTick : CartesianTick
    {
        /// <summary>
        /// How ticks are generated.
        /// </summary>
        public string Source { get; init; }
    }
}
