
using OpenSmc.Messaging;

namespace OpenSmc.Application
{
    public record ApplicationAddress(string Id, string Project);

    public abstract record ApplicationHostedAddress(ApplicationAddress ApplicationAddress) : IHostedAddressSettable
    {
        public object Host { get; init; } = ApplicationAddress;
        public object SetHost(object hostAddress)
        {
            return this with { Host = hostAddress };
        }
    }
}
