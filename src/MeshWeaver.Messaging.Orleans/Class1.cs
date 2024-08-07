using Orleans;

namespace MeshWeaver.Messaging.Orleans
{
    public class MessageHubGrain : Grain, IGrainWithStringKey
    {
        public MessageHubGrain()
        {
        }

        public override Task OnActivateAsync(CancellationToken cancellationToken)
        {
            return base.OnActivateAsync(cancellationToken);

        }
    }

}
