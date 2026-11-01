using Xunit;
using Xunit.v3;

namespace MeshWeaver.Testing.Xunit.Test;

/// <summary>
/// 🚨 The per-test bound, measured on a REAL run (MeshWeaver.Plugins#4424).
///
/// <para>This assembly runs on <see cref="MeshTestFramework"/> (declared in source), so this checks the
/// bound reaches a plain <c>[Fact]</c> through that host. The end-to-end proof, a child run whose stuck
/// tests fail by name at the bound, is <c>MeshWeaver.Graph.Test.TestTimeoutsTest</c>.</para>
/// </summary>
public class PerTestBoundTest
{
    /// <summary>
    /// THIS run is bounded too: a plain <c>[Fact]</c> carries the assembly's configured bound as its
    /// timeout. It was 0 (unbounded) before the host applied it.
    /// </summary>
    [Fact]
    public void APlainFactCarriesTheConfiguredBound()
    {
        var test = TestContext.Current.Test as IXunitTest;
        Assert.NotNull(test);
        var configured = MethodBound.Resolve(test.TestCase.TestMethod.TestClass.TestCollection.TestAssembly);
        Assert.True(configured > 0, "this assembly's xunit.runner.json declares a methodTimeout, so the bound must resolve");
        Assert.Equal(configured, test.Timeout);
    }
}
