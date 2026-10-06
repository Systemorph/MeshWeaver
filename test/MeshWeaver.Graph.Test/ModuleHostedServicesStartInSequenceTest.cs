using System;
using System.Collections.Immutable;
using System.Threading.Tasks;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// #6128 review: a module's hosted services start ONE AFTER ANOTHER, each once the previous start has
/// completed, and stop in reverse. That is the order the generic host gives root
/// <c>IHostedService</c>s, and the per-registration forwarders that <c>ModuleHostedServicesHost</c>
/// replaced inherited it. <see cref="ModuleServices.InSequence"/> is the one helper the module host's
/// start and stop, a generation's <c>StartAllHosted</c> and its <c>StopHosted</c> all run through.
/// </summary>
public class ModuleHostedServicesStartInSequenceTest
{
    [Fact]
    public async Task ASecondStep_DoesNotStart_UntilTheFirstHasCompleted()
    {
        var entries = ImmutableList<string>.Empty;
        void Add(string entry) => ImmutableInterlocked.Update(ref entries, e => e.Add(entry));
        // An unstarted task: it completes only when the test starts it, so the first step stays pending.
        var firstCompletes = new Task(() => Add("first completed"));

        var sequence = ModuleServices.InSequence(
        [
            () => { Add("first started"); return firstCompletes; },
            () => { Add("second started"); return Task.CompletedTask; },
        ]);

        entries.Should().Equal(["first started"],
            "the second hosted service must not start while the first one's start is still running");

        firstCompletes.Start();
        await sequence;

        entries.Should().Equal(["first started", "first completed", "second started"],
            "the second starts once the first has completed, in registration order");
    }

    [Fact]
    public async Task AFaultedStep_DoesNotKeepTheNextFromRunning()
    {
        var ran = false;
        await ModuleServices.InSequence(
        [
            () => Task.FromException(new InvalidOperationException("a module's start faulted")),
            () => { ran = true; return Task.CompletedTask; },
        ]);
        ran.Should().BeTrue("one module's failing start costs its own feature, never the next service's start");
    }
}
