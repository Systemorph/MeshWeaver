using OpenSmc.GridModel;
using OpenSmc.Messaging;

namespace OpenSmc.Reporting;

public class ReportingPlugin(IMessageHub hub, Func<ReportConfiguration, ReportConfiguration> reportConfiguration) : MessageHubPlugin(hub), IMessageHandler<ReportRequest>
{
    public IMessageHub MessageHub { get; init; } = hub;

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

public record ReportConfiguration;
public record ReportRequest : IRequest<ReportResponse>;

public record ReportResponse(long Version, GridOptions GridOptions);