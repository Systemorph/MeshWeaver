namespace MeshWeaver.Charting.Models.Options.Scales.Ticks
{
    public record TimeTick : CartesianTick
    {
        /// <summary>
        /// How ticks are generated.
        /// </summary>
        public string Source { get; init; }
    }
}
