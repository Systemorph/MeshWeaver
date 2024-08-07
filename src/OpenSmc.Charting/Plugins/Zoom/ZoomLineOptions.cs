using OpenSmc.Charting.Models.Options;

namespace OpenSmc.Charting.Plugins.Zoom
{
    /// <summary>
    /// Requires Zoom and Pan plugin.
    /// https://github.com/chartjs/chartjs-plugin-zoom
    /// </summary>
    public record ZoomLineOptions : ChartOptions
    {
        public Pan Pan { get; set; }

        public Zoom Zoom { get; set; }
    }
}
