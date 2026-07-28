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
/// Marks a hub test method. In non-DEBUG builds a timeout is applied so that a wedged hub fails
/// the test instead of hanging the run.
///
/// <para>🚨 The budget must exceed what the MESH FIXTURE costs on a loaded CI runner, or the test
/// measures the runner instead of the code. It was 5s, and 5s is not achievable there: a CI log for
/// <c>MeshNodeVersionSyncTest</c> (2026-07-27) shows class init at 19:59:22.595 and dispose at
/// 19:59:30.720 — <b>8.1s of fixture</b> before the body runs at all. That produced a recurring
/// main-red flake with no defect behind it, and the same trap bit a heartbeat test written the same
/// night ("Test execution timed out after 5000 milliseconds", passing locally where no budget is
/// enforced).</para>
///
/// <para>30s matches the runner-wide <c>methodTimeout</c> and still fails a genuine wedge well
/// before the run hangs — a wedged read surfaces its own <c>GetMeshNode … timed out after 60.0s</c>
/// first, so nothing is masked by the larger budget.</para>
/// </summary>
public sealed class HubFactAttribute : FactAttribute
{
    /// <summary>
    /// Initializes a new instance and sets the test timeout to 30000 milliseconds — above the
    /// measured CI fixture cost, in line with the runner-wide <c>methodTimeout</c>.
    /// </summary>
    public HubFactAttribute()
    {
        Timeout = 30000;
    }
};
#endif
