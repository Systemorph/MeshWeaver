using MeshWeaver.Charting.Models;

namespace MeshWeaver.Charting.Plugins.Zoom
{
    /// <summary>
    /// Requires Zoom and Pan plugin.
    /// https://github.com/chartjs/chartjs-plugin-zoom
    /// </summary>
    public record ZoomPolarOptions : PolarOptions
    {
        public Pan Pan { get; set; }

        public Zoom Zoom { get; set; }
    }
}
