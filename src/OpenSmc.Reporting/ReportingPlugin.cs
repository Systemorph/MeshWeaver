using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Collections;
using OpenSmc.Data;
using OpenSmc.DataCubes;
using OpenSmc.GridModel;
using OpenSmc.Messaging;
using OpenSmc.Pivot.Builder;
using OpenSmc.Reporting.Builder;

namespace OpenSmc.Reporting;

public class ReportingPlugin(IMessageHub hub, Func<ReportConfiguration, ReportConfiguration> reportConfiguration) : MessageHubPlugin(hub), IMessageHandler<ReportRequest>
{
    private readonly IWorkspace workspace = hub.ServiceProvider.GetRequiredService<IWorkspace>();

    private ReportConfiguration Configuration = reportConfiguration(new());

    public IMessageDelivery HandleMessage(IMessageDelivery<ReportRequest> request)
    {
        GridOptions gridOptions = null;
        try
        {
            gridOptions = Configuration.dataCubeConfig.GetGridOptions(workspace);
            //IEnumerable<object> data;
            //var grid = PivotFactory.ForDataCube(data).ToTable().Execute();
        }
        catch (Exception ex)
        {
            return request.Failed($"Failed to produce report: {ex.Message}");
        }
        finally
        {
            Hub.Post(new ReportResponse(Hub.Version, gridOptions ?? new GridOptions()), o => o.ResponseFor(request));
        }

        return request.Processed();
    }
}

public record ReportConfiguration
{
    internal ReportDataCubeConfiguration dataCubeConfig;
    public ReportConfiguration WithDataCubeOn<T>(Func<IWorkspace, IEnumerable<T>> dataFunc, Func<DataCubePivotBuilder<IDataCube<T>, T, T, T>, DataCubeReportBuilder<IDataCube<T>, T, T, T>> reportFunc) 
        => this with { dataCubeConfig = new ReportDataCubeConfiguration<T>(dataFunc, reportFunc), };
}

public abstract record ReportDataCubeConfiguration
{
    internal abstract GridOptions GetGridOptions(IWorkspace workspace);
}

public record ReportDataCubeConfiguration<T>(Func<IWorkspace, IEnumerable<T>> dataFunc, Func<DataCubePivotBuilder<IDataCube<T>, T, T, T>, DataCubeReportBuilder<IDataCube<T>, T, T, T>> reportFunc) : ReportDataCubeConfiguration
{
    internal override GridOptions GetGridOptions(IWorkspace workspace)
    {
        var data = dataFunc(workspace).ToDataCube().RepeatOnce();
        var result = reportFunc(PivotFactory.ForDataCubes(data)).Execute();
        return result;
    }
}

public record ReportRequest : IRequest<ReportResponse>;

public record ReportResponse(long Version, GridOptions GridOptions);