using OpenSmc.Activities;
using OpenSmc.Messaging;

namespace OpenSmc.Reporting;

public class ReportingPlugin(IMessageHub hub, Func<ReportConfiguration, ReportConfiguration> reportConfiguration) : MessageHubPlugin(hub), IMessageHandlerAsync<ReportRequest>
{
    public IMessageHub MessageHub { get; init; } = hub;

    public Task<IMessageDelivery> HandleMessageAsync(IMessageDelivery<ReportRequest> request, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}

public record ReportConfiguration;
public record ReportRequest : IRequest<ReportResponse>;

public record ReportResponse(long Version, ActivityLog Log);