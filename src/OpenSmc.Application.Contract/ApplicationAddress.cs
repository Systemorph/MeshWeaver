
using OpenSmc.Messaging;

namespace OpenSmc.Application
{
    // domain name + env pair uniquely identifies the domain
    // TODO V10: add "string ClientId" (05.06.2024, Alexander Kravets)
    public record ApplicationAddress(string Name, string Environment)
    {
        public override string ToString()
            => $"app/{Name}/{Environment}";
    }

    public abstract record ApplicationHostedAddress(ApplicationAddress ApplicationAddress) : IHostedAddressSettable
    {
        public object Host { get; init; } = ApplicationAddress;
        public object SetHost(object hostAddress)
        {
            return this with { Host = hostAddress };
        }

    }
}
