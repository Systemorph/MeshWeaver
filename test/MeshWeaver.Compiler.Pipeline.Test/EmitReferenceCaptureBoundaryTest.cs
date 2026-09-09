using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Compiler;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.Graph.Test;

public sealed class EmitReferenceCaptureBoundaryTest
{
    [Fact]
    public void SchedulingFailureCannotReplaceTheOriginalEmitException()
    {
        var original = new InvalidOperationException("original compiler failure");
        var compilation = CSharpCompilation.Create("Broken",
            [CSharpSyntaxTree.ParseText("public class C {}")], [new ThrowingReference(original)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Exception? callbackError = null;
        var caught = Record.Exception(() => EmitPipeline.EmitCompilationToDirectory(
            compilation, "Broken", "Test/Broken", Path.GetTempPath(), [], CancellationToken.None,
            (_, failure) =>
            {
                callbackError = failure;
                throw new IOException("diagnostic scheduling failure");
            }));

        caught.Should().BeSameAs(original);
        callbackError.Should().BeSameAs(original);
        original.Data[EmitPipeline.EmitCanaryDataKey].Should().NotBeNull();
        original.Data[EmitPipeline.EmitReferenceCaptureDataKey].Should()
            .Be("incomplete:scheduling-IOException");
        NodeTypeCompilationHelpers.SummarizeCompileError(null, original).Should()
            .Contain("emit-reference-capture: incomplete:scheduling-IOException");
    }

    [Fact]
    public void ExistingEmitEntryPointDoesNotRequestCapture()
    {
        var original = new InvalidOperationException("original");
        var compilation = CSharpCompilation.Create("Broken",
            [CSharpSyntaxTree.ParseText("public class C {}")], [new ThrowingReference(original)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var caught = Record.Exception(() => EmitPipeline.EmitCompilationToDirectory(
            compilation, "Broken", "Test/Broken", Path.GetTempPath(), CancellationToken.None));

        caught.Should().BeSameAs(original);
        original.Data.Contains(EmitPipeline.EmitReferenceCaptureDataKey).Should().BeFalse();
    }

    [Fact]
    public void OrdinaryCompilerDiagnosticsDoNotConsumeTheFailureCapture()
    {
        var requested = false;
        var compilation = EmitPipeline.CreateEmitCompilation("this is invalid source", "Invalid",
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)], "", CancellationToken.None);

        var caught = Record.Exception(() => EmitPipeline.EmitCompilationToDirectory(compilation,
            "Invalid", "Test/Invalid", Path.GetTempPath(), [], CancellationToken.None,
            (_, _) => requested = true));

        caught.Should().BeOfType<CompilationException>();
        requested.Should().BeFalse("source diagnostics are not thrown-from-Emit runtime failures");
    }

    [Fact]
    public async Task AnAlreadyDisposedOwnerDoesNotStartDiagnosticIo()
    {
        var directory = Path.Combine(Path.GetTempPath(), "emit-disposed-" + Guid.NewGuid().ToString("N"));
        var pool = new IoPool(1);
        try
        {
            var scheduler = new EmitReferenceCaptureScheduler(pool, directory, item => item.Dispose());
            var error = new InvalidOperationException("original");
            scheduler.Schedule(CSharpCompilation.Create("Unused"), error);
            error.Data[EmitPipeline.EmitReferenceCaptureDataKey].Should().Be("incomplete:owner-disposed");
            (await pool.InvokeBlocking(_ => Directory.Exists(directory)).Timeout(TimeSpan.FromSeconds(10)))
                .Should().BeFalse();
        }
        finally
        {
            pool.Dispose();
            (await pool.Disposed.FirstAsync().Timeout(TimeSpan.FromSeconds(10))).Should().Be(0);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task CaptureUsesTheFilePoolAndReleasesItsOwnedSubscription()
    {
        var directory = Path.Combine(Path.GetTempPath(), "emit-boundary-" + Guid.NewGuid().ToString("N"));
        var pool = new IoPool(1);
        using var owner = new CompositeDisposable();
        using var actor = new EventLoopScheduler();
        SerialDisposable? registered = null;
        var scheduler = new EmitReferenceCaptureScheduler(pool, directory, disposable =>
        {
            registered = (SerialDisposable)disposable;
            owner.Add(disposable);
        });
        var compilation = CSharpCompilation.Create("NoSourceIsCaptured",
            references: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        var failure = new InvalidOperationException("not recorded");
        try
        {
            var actorThread = await Observable.Start(() =>
            {
                scheduler.Schedule(compilation, failure);
                return Environment.CurrentManagedThreadId;
            }, actor).Timeout(TimeSpan.FromSeconds(10));

            // The real single-slot file pool queues this read after the capture. The
            // actor itself never waits for a file or a diagnostic completion.
            var json = await pool.InvokeBlocking(_ => File.ReadAllText(Path.Combine(directory, "manifest.json")))
                .Timeout(TimeSpan.FromSeconds(10));
            using var manifest = JsonDocument.Parse(json);
            manifest.RootElement.GetProperty("complete").GetBoolean().Should().BeTrue();
            var captureId = manifest.RootElement.GetProperty("captureId").GetString();
            captureId.Should().NotBeNullOrEmpty();
            NodeTypeCompilationHelpers.SummarizeCompileError(null, failure).Should().Contain($"id={captureId}");
            manifest.RootElement.GetProperty("runtime").GetProperty("managedThreadId").GetInt32()
                .Should().NotBe(actorThread);
            registered.Should().NotBeNull();
            await Observable.Interval(TimeSpan.FromMilliseconds(10))
                .Where(_ => registered!.IsDisposed).Take(1).Timeout(TimeSpan.FromSeconds(10));
            registered!.IsDisposed.Should().BeTrue("a completed diagnostic must release its owner");
        }
        finally
        {
            owner.Dispose();
            pool.Dispose();
            (await pool.Disposed.FirstAsync().Timeout(TimeSpan.FromSeconds(10))).Should().Be(0);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class ThrowingReference(Exception failure) : PortableExecutableReference(MetadataReferenceProperties.Assembly)
    {
        protected override DocumentationProvider CreateDocumentationProvider() => DocumentationProvider.Default;
        protected override Metadata GetMetadataImpl() => throw failure;
        protected override PortableExecutableReference WithPropertiesImpl(MetadataReferenceProperties properties)
            => new ThrowingReference(failure);
    }
}
