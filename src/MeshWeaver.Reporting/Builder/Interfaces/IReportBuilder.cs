using MeshWeaver.GridModel;

namespace MeshWeaver.Reporting.Builder.Interfaces
{
    public interface IReportBuilder<out TReportBuilder>
        where TReportBuilder : IReportBuilder<TReportBuilder>
    {
        public TReportBuilder WithOptions(Func<GridOptions, GridOptions> gridOptions);
        public GridOptions Execute();
    }
}
