using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// MeshWeaver#7464 — the caps the partitioned Postgres backend's write path relies on: the
/// provider-wide write BUDGET its per-schema write lanes take slots of, which keeps their total
/// below the shared connection pool, and the unchanged cap-1 <c>pg:</c> rule.
/// </summary>
public class PostgresWriteLanesAndBudgetCapsTest
{
    [Fact]
    public void TheProviderWideWriteLane_IsUnchanged()
    {
        var options = new IoPoolOptions();
        options.MaxConcurrencyFor($"{IoPoolNames.PostgresAdapterPrefix}Postgres")
            .Should().Be(1, "the provider-wide lane the event log and the instance store keep is unchanged");
    }

    [Fact]
    public void TheWriteBudget_IsItsOwnCap_NotTheCapOneLaneRule()
    {
        new IoPoolOptions().MaxConcurrencyFor($"{IoPoolNames.PostgresWriteBudgetPrefix}Postgres")
            .Should().Be(16, "16 writes + 16 reads stay under the portal's MaxPoolSize of 50");
        new IoPoolOptions { PostgresWrite = 4 }
            .MaxConcurrencyFor($"{IoPoolNames.PostgresWriteBudgetPrefix}Postgres")
            .Should().Be(4, "the budget is an option, so a host with a smaller connection pool can lower it");
        new IoPoolOptions().MaxConcurrencyFor($"{IoPoolNames.PostgresReadAdapterPrefix}Postgres")
            .Should().Be(16, "the read pool is untouched");
    }
}
