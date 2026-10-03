using System.Collections.Generic;
using MeshWeaver.Kernel.Hub;
using MeshWeaver.Layout;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// A script's return value earns a transcript line only when it describes itself.
/// <para>The kernel logged <c>returnValue.ToString()</c> for everything but a control, so a
/// script returning a dictionary put
/// <c>System.Collections.Generic.Dictionary`2[System.String,System.Object]</c> into its
/// activity log — and, as the log's last line, into the activity page's headline. The value
/// is carried by <c>ActivityLog.ReturnValue</c>; a type name is not output.</para>
/// </summary>
public class ScriptReturnValueOutputLineTest
{
    private sealed class Plain;

    private sealed record Described(string Name);

    public static TheoryData<object> SaysNothingAboutItself => new()
    {
        new Dictionary<string, object> { ["sent"] = true },
        new List<int> { 1, 2, 3 },
        new[] { 1, 2, 3 },
        new Plain(),
    };

    [Theory]
    [MemberData(nameof(SaysNothingAboutItself))]
    public void AValueThatPrintsOnlyItsTypeName_EarnsNoLine(object value) =>
        Assert.Null(KernelExecutor.OutputLine(value));

    [Fact]
    public void AControl_EarnsNoLine_ItIsRendered() =>
        Assert.Null(KernelExecutor.OutputLine(Controls.Label("x")));

    [Fact]
    public void AScalar_AString_ARecord_AndAnAnonymousObject_AreTheOutput()
    {
        Assert.Equal("2", KernelExecutor.OutputLine(1 + 1));
        Assert.Equal("done", KernelExecutor.OutputLine("done"));
        Assert.Equal("Described { Name = a }", KernelExecutor.OutputLine(new Described("a")));
        Assert.Equal("{ sent = True }", KernelExecutor.OutputLine(new { sent = true }));
    }

    [Fact]
    public void AStringThatSpellsItsOwnTypeName_IsStillOutput() =>
        Assert.Equal("System.String", KernelExecutor.OutputLine("System.String"));
}
