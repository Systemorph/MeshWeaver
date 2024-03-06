using Microsoft.Extensions.DependencyInjection;
using OpenSmc.Data;
using OpenSmc.DataCubes;
using OpenSmc.GridModel;
using OpenSmc.Messaging;
using OpenSmc.Pivot.Builder;
using OpenSmc.Reporting.Builder;

namespace OpenSmc.Reporting;

public class ReportingPlugin : MessageHubPlugin, IMessageHandler<ReportRequest>
{
    // TODO V10: inject scope factory (06.03.2024, Ekaterina Mishina)
    private readonly IWorkspace workspace;

    public ReportConfiguration Configuration;
    public ReportingPlugin(IMessageHub hub, Func<ReportConfiguration, ReportConfiguration> importConfiguration) : base(hub)
    {
        workspace = hub.ServiceProvider.GetRequiredService<IWorkspace>();
        Configuration = importConfiguration.Invoke(new(hub, workspace)).Build();
    }

    public IMessageDelivery HandleMessage(IMessageDelivery<ReportRequest> request)
    {
        try
        {
            //IEnumerable<object> data;
            //var grid = PivotFactory.ForDataCube(data).ToTable().Execute();
        }
        catch (Exception ex)
        {
            return request.Failed($"Failed to produce report: {ex.Message}");
        }
        finally
        {
            Hub.Post(new ReportResponse(Hub.Version, new GridOptions()), o => o.ResponseFor(request));
        }

        return request.Processed();
    }
}

public record ReportConfiguration(IMessageHub Hub, IWorkspace Workspace)
{
    public ReportConfiguration Build() => this;

    public ReportConfiguration WithType<T>(
        Func<ReportConfiguration<T>, ReportConfiguration<T>> reportTypeConfigFunc) =>
        this;// with { ReportTypeConfigFunc = reportTypeConfigFunc };

    //public Func<ReportConfiguration<object>, ReportConfiguration<object>> ReportTypeConfigFunc { get; set; }
}

public record ReportConfiguration<T>
{
    public Func<IWorkspace, IEnumerable<IDataCube<T>>> GetDataCubesFunc { get; set; }
    public Func<DataCubePivotBuilder<IDataCube<T>, T, T, T>, DataCubeReportBuilder<IDataCube<T>, T, T, T>> ReportBuilderFunc { get; set; }

    public ReportConfiguration<T> WithData(Func<IWorkspace, IEnumerable<IDataCube<T>>> getDataCubesFunc) =>
        this with { GetDataCubesFunc = getDataCubesFunc };

    public ReportConfiguration<T> WithReportBuilder(
        Func<DataCubePivotBuilder<IDataCube<T>, T, T, T>, DataCubeReportBuilder<IDataCube<T>, T, T, T>>
            reportBuilderFunc) =>
        this with { ReportBuilderFunc = reportBuilderFunc };
}

public record ReportRequest : IRequest<ReportResponse>;

public record ReportResponse(long Version, GridOptions GridOptions);