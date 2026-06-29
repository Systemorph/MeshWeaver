using Xunit;

namespace MeshWeaver.Fixture;

#if DEBUG
/// <summary>
/// Marks a hub test method. In DEBUG builds no timeout is applied, allowing
/// unattended breakpoint debugging of hub message flow.
/// </summary>
public class HubFactAttribute : FactAttribute;
#else
/// <summary>
/// Marks a hub test method. In non-DEBUG builds a 5 second timeout is applied
/// so that a wedged hub fails the test fast instead of hanging the run.
/// </summary>
public sealed class HubFactAttribute : FactAttribute
{
    /// <summary>
    /// Initializes a new instance and sets the test timeout to 5000 milliseconds.
    /// </summary>
    public HubFactAttribute()
    {
        Timeout = 5000;
    }
};
#endif
