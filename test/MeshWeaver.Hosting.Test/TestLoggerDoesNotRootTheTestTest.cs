using System;
using System.Runtime.CompilerServices;
using MeshWeaver.Fixture;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// A logger handed out by a test's container must not keep the test instance alive.
///
/// <para>Loggers outlive the mesh they were created for. A pending timer's captured
/// <c>ExecutionContext</c> and <c>IHttpClientFactory</c>'s handler pipeline both hold one. When the
/// logger provider's output-helper closure captured <c>this</c>, each such holder reached the test
/// instance. From there it reached the instance's <c>Services</c>, the <c>MeshBuilder</c> and the
/// whole disposed mesh. A heap dump of <c>Memex.Portal.Shared.Test</c> showed that path
/// (<c>XUnitFileLogger</c> → <c>Func&lt;XUnitFileOutputHelper&gt;</c> → the test class) on every
/// retained mesh.</para>
///
/// <para>The logger is checked to be a working file logger before the measurement, so a pass means
/// the closure does not root the test, not that the logger was never wired.</para>
/// </summary>
public class TestLoggerDoesNotRootTheTestTest
{
    [Fact]
    public void ALoggerFromTheTestContainerDoesNotKeepTheTestInstanceAlive()
    {
        var (test, logger) = CreateATestAndTakeALogger();

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        test.IsAlive.Should().BeFalse(
            "only the logger is still held. The provider closes over the output helper, not over "
            + "the test, so the test and its Services must be collectable");
        GC.KeepAlive(logger);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Test, ILogger Logger) CreateATestAndTakeALogger()
    {
        var test = new ProbeTest(TestContext.Current.TestOutputHelper!);
        var provider = test.Services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("probe");
        provider.GetServices<ILoggerProvider>().Should().ContainSingle(
            "the test container's only logger provider is the xUnit file logger, or this test "
            + "measures nothing");
        return (new WeakReference(test), logger);
    }

    private sealed class ProbeTest(ITestOutputHelper output) : TestBase(output);
}
