using Xunit;

namespace OpenSmc.Hub.Fixture;

#if DEBUG
public class HubFactAttribute : FactAttribute;
#else
public sealed class HubFactAttribute : FactAttribute
{
    public HubFactAttribute()
    {
        Timeout = 5000;
    }
};
#endif
