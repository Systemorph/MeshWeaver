using MeshWeaver.Charting.Models.Options;

namespace MeshWeaver.Charting.Models.Bar;

public interface IChartOptionsConfiguration
{
    ChartOptions Configure(ChartOptions options);
}
