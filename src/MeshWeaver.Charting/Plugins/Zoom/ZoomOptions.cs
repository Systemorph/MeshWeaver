using MeshWeaver.Charting.Models.Options;

namespace MeshWeaver.Charting.Plugins.Zoom
{
    /// <summary>
    /// Requires Zoom and Pan plugin.
    /// https://github.com/chartjs/chartjs-plugin-zoom
    /// </summary>
    public record ZoomOptions : ChartOptions
    {
        public Pan Pan { get; set; }

        public Zoom Zoom { get; set; }
    }
}
