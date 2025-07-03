using MeshWeaver.Charting.Models;
using MeshWeaver.Charting.Models.Polar;

namespace MeshWeaver.Charting.Plugins.Zoom
{
    /// <summary>
    /// Requires Zoom and Pan plugin.
    /// https://github.com/chartjs/chartjs-plugin-zoom
    /// </summary>
    public record ZoomPolarOptions : PolarOptions
    {
        public Pan Pan { get; set; } = null!;

        public Zoom Zoom { get; set; } = null!;
    }
}
