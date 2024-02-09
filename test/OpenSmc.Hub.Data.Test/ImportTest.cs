using OpenSmc.Data;
using OpenSmc.Messaging;
using OpenSmc.ServiceProvider;

namespace OpenSmc.Hub.Data.Test;

public record ImportRequest;

public class ImportPlugin : MessageHubPlugin, IMessageHandler<ImportRequest>
{
    [Inject] IWorkspace workspace;

    public ImportPlugin(IMessageHub hub) : base(hub)
    {
    }

    IMessageDelivery IMessageHandler<ImportRequest>.HandleMessage(IMessageDelivery<ImportRequest> request)
    {
        // TODO V10: Mise-en-place have been done
        var someData = workspace.Query<object>().ToArray();
        workspace.Update(new[] { new object(), });

        return request.Processed();
    }
}

public class ImportTest
{
}
