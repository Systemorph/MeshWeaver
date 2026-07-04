using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Serialization.Test;

/// <summary>
/// Test type for cyclic / deep object graphs. <see cref="Self"/> is typed <see langword="object"/>
/// so serialization routes through the hub's polymorphic chain (ObjectPolymorphicConverter).
/// </summary>
public record SelfReferencing(string Name)
{
    /// <summary>The link that closes the cycle / builds the chain.</summary>
    public object? Self { get; set; }
}

/// <summary>
/// Pins the serializer self-reference defect: serializing a SELF-REFERENCING object through the
/// hub's real <see cref="JsonSerializerOptions"/> must surface a catchable <see cref="JsonException"/>
/// naming the type — never unbounded recursion that exhausts the native stack and kills the process
/// (exit 134 / SIGABRT). Root cause: ObjectPolymorphicConverter (and the ImmutableDictionary
/// converter) recursed per object-graph edge through FRESH serializer sessions
/// (<c>JsonSerializer.Serialize(value → string)</c> / <c>SerializeToNode</c>), each of which resets
/// <c>Utf8JsonWriter.CurrentDepth</c> to 0 — so the MaxDepth(64) guard never tripped while the C#
/// call stack grew per edge. The fix (SerializationDepthGuard) carries accumulated depth across
/// those nested sessions so MaxDepth semantics hold again.
/// </summary>
public class SerializerSelfReferenceTest(ITestOutputHelper output) : HubTestBase(output)
{
    internal const string ProbeEnvVar = "MW_SERIALIZER_CYCLE_PROBE";
    internal const string ProbeOutEnvVar = "MW_SERIALIZER_CYCLE_PROBE_OUT";
    private const string JsonExceptionMarker = "JSON_EXCEPTION:";
    private const string NoExceptionMarker = "NO_EXCEPTION:";

    /// <inheritdoc />
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration).WithTypes(typeof(SelfReferencing));

    /// <summary>
    /// A DEEP (non-cyclic, finite) chain of object-typed edges must be rejected with a
    /// <see cref="JsonException"/> that names the offending type once accumulated depth exceeds
    /// MaxDepth — the same guard that turns a true cycle into an error instead of a SIGABRT.
    /// This variant is host-safe in both red and green states (the chain is finite), so it can
    /// run in-suite; the true-cycle case runs in a child process below.
    /// Runs on a dedicated wide-stack thread so the pre-fix red state (recursion one C# stack
    /// level per edge) is a clean assertion failure rather than a stack-size gamble.
    /// </summary>
    [Fact]
    public void DeepObjectGraph_ThrowsJsonExceptionNamingTheType()
    {
        var host = GetHost();
        object chain = new SelfReferencing("leaf");
        for (var i = 0; i < 100; i++)
            chain = new SelfReferencing($"level{i}") { Self = chain };

        Exception? caught = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    JsonSerializer.Serialize(chain, host.JsonSerializerOptions);
                }
                catch (Exception ex)
                {
                    caught = ex;
                }
            },
            maxStackSize: 16 * 1024 * 1024);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue("serialization of a depth-101 chain must terminate");

        caught.Should().NotBeNull("a graph nested beyond MaxDepth must be rejected, not serialized by resetting writer depth per level");
        caught.Should().BeOfType<JsonException>();
        caught!.Message.Should().Contain(nameof(SelfReferencing),
            "the depth-guard error must name the offending type so a cycle is diagnosable");
        caught.Message.Should().Contain("MaxDepth");
    }

    /// <summary>
    /// Child-process payload for <see cref="SelfReferencingObject_SurfacesJsonException_InsteadOfKillingTheProcess"/>.
    /// No-op unless <see cref="ProbeEnvVar"/> is set: pre-fix, serializing a true cycle exhausts the
    /// native stack and SIGABRTs the whole process — uncatchable — so it must only ever run in a
    /// disposable child process, never in the shared test host.
    /// </summary>
    [Fact]
    public void CycleProbe()
    {
        if (Environment.GetEnvironmentVariable(ProbeEnvVar) != "1")
            return; // only meaningful when driven by the parent test in a child process

        var host = GetHost();
        var cyclic = new SelfReferencing("root");
        cyclic.Self = cyclic; // the cycle

        string result;
        try
        {
            var json = JsonSerializer.Serialize(cyclic, host.JsonSerializerOptions);
            result = NoExceptionMarker + json[..Math.Min(200, json.Length)];
        }
        catch (JsonException ex)
        {
            result = JsonExceptionMarker + ex.Message;
        }

        var outFile = Environment.GetEnvironmentVariable(ProbeOutEnvVar);
        if (outFile is not null)
            File.WriteAllText(outFile, result);
    }

    /// <summary>
    /// THE repro for the SIGABRT defect, host-protected: runs <see cref="CycleProbe"/> (a true
    /// self-reference serialized with the hub's real options) in a CHILD process — this same
    /// xunit.v3 executable filtered to that one method — so the red state is an assertable exit
    /// code (134 = SIGABRT, native stack exhausted), not a dead test host. Green: the child
    /// survives and reports a catchable <see cref="JsonException"/> naming the type.
    /// </summary>
    [Fact]
    public async Task SelfReferencingObject_SurfacesJsonException_InsteadOfKillingTheProcess()
    {
        var assemblyPath = typeof(SerializerSelfReferenceTest).Assembly.Location;
        var resultFile = Path.Combine(Path.GetTempPath(), $"mw-cycle-probe-{Guid.NewGuid():N}.txt");
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(assemblyPath)!,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(assemblyPath);
        psi.ArgumentList.Add("-method");
        psi.ArgumentList.Add($"{typeof(SerializerSelfReferenceTest).FullName}.{nameof(CycleProbe)}");
        psi.Environment[ProbeEnvVar] = "1";
        psi.Environment[ProbeOutEnvVar] = resultFile;

        try
        {
            using var probe = Process.Start(psi)!;
            var stdoutTask = probe.StandardOutput.ReadToEndAsync();
            var stderrTask = probe.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try
            {
                await probe.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                probe.Kill(entireProcessTree: true);
                throw new TimeoutException("Cycle probe child process did not exit within 45s.");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            Output.WriteLine($"probe exit code: {probe.ExitCode}");
            Output.WriteLine($"probe stdout:\n{stdout}");
            Output.WriteLine($"probe stderr:\n{stderr}");

            File.Exists(resultFile).Should().BeTrue(
                $"the probe child must SURVIVE serializing a self-referencing object and record an outcome "
                + $"(exit code {probe.ExitCode}; 134 = SIGABRT — the converter chain recursed per graph edge "
                + "without consuming serializer depth and exhausted the native stack)");
            var result = await File.ReadAllTextAsync(resultFile, TestContext.Current.CancellationToken);
            Output.WriteLine($"probe result: {result}");
            result.Should().StartWith(JsonExceptionMarker,
                "a cycle must surface as a catchable JsonException — never serialize 'successfully' and never crash");
            result.Should().Contain(nameof(SelfReferencing), "the error must name the offending type");
            probe.ExitCode.Should().Be(0, "the probe test itself must pass in the child");
        }
        finally
        {
            if (File.Exists(resultFile))
                File.Delete(resultFile);
        }
    }
}
