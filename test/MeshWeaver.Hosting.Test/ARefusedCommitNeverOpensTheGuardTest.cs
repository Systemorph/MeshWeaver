using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// A commit REFUSED by an open deletion scope never lets another write through the storage guard
/// (Systemorph/MeshWeaver#7346 review). <see cref="RecentlyDeletedRegistry.IsAdmittedAtCommit"/> is the
/// guard's bypass for a write admitted at its owner's commit; it was published BEFORE the scope check,
/// so for the instant between the two a refused commit made every write to its path pass the open
/// scope — and a deletion that had already finished waiting for admitted writes could then be
/// overtaken by a write that recreates the row. The bypass is now published only once the check has
/// accepted the commit.
///
/// <para>The window lies inside one method call, so the interleaving is driven by two threads: one
/// keeps asking for commits the open scope refuses, the other keeps asking the guard's question for
/// the same path. Before the fix the guard's question answered <c>true</c> within the first few
/// thousand iterations; after it, never.</para>
/// </summary>
public class ARefusedCommitNeverOpensTheGuardTest
{
    private const int Commits = 200_000;

    [Fact]
    public void ARefusedCommitIsNeverVisibleAsABypass()
    {
        var registry = new RecentlyDeletedRegistry();
        using var scope = registry.BeginSubtreeDeletion("acme/space");
        const string path = "acme/space/node";

        // Negative control on the instrument: an ACCEPTED commit outside the scope IS visible.
        using (var accepted = registry.AdmitCommit("acme/other", out _))
        {
            accepted.Should().NotBeNull();
            registry.IsAdmittedAtCommit("acme/other").Should().BeTrue();
        }

        var committing = 1;
        var bypassesSeen = 0;
        var refusedCommits = 0;
        Parallel.Invoke(
            () =>
            {
                try
                {
                    for (var i = 0; i < Commits; i++)
                        if (registry.AdmitCommit(path, out _) is null)
                            refusedCommits++;
                }
                finally
                {
                    Volatile.Write(ref committing, 0);
                }
            },
            () =>
            {
                while (Volatile.Read(ref committing) != 0)
                    if (registry.IsAdmittedAtCommit(path))
                        Interlocked.Increment(ref bypassesSeen);
            });

        refusedCommits.Should().Be(Commits, "every commit under the open scope is refused");
        bypassesSeen.Should().Be(0,
            "a commit the scope refused must never let a write to its path through the guard");
    }
}
