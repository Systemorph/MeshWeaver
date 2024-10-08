using Xunit;

namespace MeshWeaver.Fixture;

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
