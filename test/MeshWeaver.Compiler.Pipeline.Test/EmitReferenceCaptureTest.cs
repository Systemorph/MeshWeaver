using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using MeshWeaver.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

public sealed class EmitReferenceCaptureTest : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "emit-reference-test-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(null, "1", "/tmp/runner")]
    [InlineData("false", "1", "/tmp/runner")]
    [InlineData("true", null, "/tmp/runner")]
    [InlineData("true", "0", "/tmp/runner")]
    [InlineData("true", "1", null)]
    [InlineData("true", "1", "relative-runner")]
    public void CaptureIsOffUnlessExplicitlyEnabledInCi(string? githubActions, string? enabled, string? runnerTemp)
    {
        Assert.Null(EmitReferenceCapture.GetCiDirectory(key => key switch
        {
            "GITHUB_ACTIONS" => githubActions,
            "MW_CAPTURE_EMIT_REFERENCES" => enabled,
            "RUNNER_TEMP" => runnerTemp,
            _ => throw new InvalidOperationException("Unexpected environment read: " + key)
        }));
    }

    [Fact]
    public void EnabledCiCaptureUsesOnlyRunnerTemporaryArtifacts()
    {
        var result = EmitReferenceCapture.GetCiDirectory(key => key switch
        {
            "GITHUB_ACTIONS" => "true",
            "MW_CAPTURE_EMIT_REFERENCES" => "1",
            "RUNNER_TEMP" => directory,
            _ => throw new InvalidOperationException("Unexpected environment read: " + key)
        });
        Assert.Equal(Path.Combine(directory, "straggler-logs", "emit-reference-capture", "process-" + Environment.ProcessId), result);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void CapturedReferencesRetainOrderPropertiesAndExactPeBytes()
    {
        var path = typeof(object).Assembly.Location;
        MetadataReference[] references =
        [
            MetadataReference.CreateFromFile(path, new MetadataReferenceProperties(
                MetadataImageKind.Assembly, ImmutableArray.Create("Second", "First"), embedInteropTypes: true)),
            MetadataReference.CreateFromFile(path)
        ];

        Assert.Equal("captured", EmitReferenceCapture.Capture(references, directory, new InvalidOperationException("private exception detail"), CancellationToken.None));
        using var manifest = ReadManifest();
        Assert.True(manifest.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal("complete", manifest.RootElement.GetProperty("reason").GetString());
        var entries = manifest.RootElement.GetProperty("references").EnumerateArray().ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Equal(new[] { "Second", "First" }, entries[0].GetProperty("aliases").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.True(entries[0].GetProperty("embedInteropTypes").GetBoolean());
        Assert.Empty(entries[1].GetProperty("aliases").EnumerateArray());
        Assert.False(entries[1].GetProperty("embedInteropTypes").GetBoolean());
        var bytes = File.ReadAllBytes(path);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        for (var ordinal = 0; ordinal < entries.Length; ordinal++)
        {
            var entry = entries[ordinal];
            Assert.Equal(ordinal, entry.GetProperty("ordinal").GetInt32());
            Assert.Equal("captured", entry.GetProperty("status").GetString());
            Assert.Equal("Assembly", entry.GetProperty("kind").GetString());
            Assert.Equal(hash, entry.GetProperty("sha256").GetString(), ignoreCase: true);
            Assert.Equal(bytes.LongLength, entry.GetProperty("size").GetInt64());
            Assert.False(string.IsNullOrEmpty(entry.GetProperty("fileAssemblyIdentity").GetString()));
            Assert.Equal(entry.GetProperty("fileMvids").GetRawText(), entry.GetProperty("metadataMvids").GetRawText());
            var capturedFile = entry.GetProperty("file").GetString();
            Assert.NotNull(capturedFile);
            Assert.Equal(Path.GetFileName(capturedFile), capturedFile);
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(directory, capturedFile)));
        }
    }

    [Fact]
    public void UnavailableAndUnsupportedReferencesMakeCaptureExplicitlyIncomplete()
    {
        Directory.CreateDirectory(directory);
        var missingPath = Path.Combine(directory, "deleted-input.dll");
        File.Copy(typeof(object).Assembly.Location, missingPath);
        var missing = MetadataReference.CreateFromFile(missingPath);
        File.Delete(missingPath);
        var image = MetadataReference.CreateFromImage(File.ReadAllBytes(typeof(object).Assembly.Location));
        var compilation = CSharpCompilation.Create("SensitiveSource", [CSharpSyntaxTree.ParseText("class PrivateSourceMarker {}")]).ToMetadataReference();
        MetadataReference[] references = [missing, image, compilation, MetadataReference.CreateFromFile(typeof(object).Assembly.Location)];

        Assert.Equal("incomplete", EmitReferenceCapture.Capture(references, directory, new InvalidOperationException("private exception detail"), CancellationToken.None));
        using var manifest = ReadManifest();
        Assert.False(manifest.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal("partial", manifest.RootElement.GetProperty("reason").GetString());
        Assert.Equal(new[] { "missing-file", "unsupported-reference", "unsupported-reference", "captured" },
            manifest.RootElement.GetProperty("references").EnumerateArray().Select(x => x.GetProperty("status").GetString()).ToArray());
        var json = File.ReadAllText(Path.Combine(directory, "manifest.json"));
        Assert.DoesNotContain("PrivateSourceMarker", json);
        Assert.DoesNotContain("private exception detail", json);
        Assert.DoesNotContain("stackTrace", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("environment", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PreCancelledCaptureWritesIncompleteVerdictWithoutCopyingReferences()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Equal("incomplete", EmitReferenceCapture.Capture(
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)], directory,
            new InvalidOperationException(), cancellation.Token));
        using var manifest = ReadManifest();
        Assert.False(manifest.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal("cancelled", manifest.RootElement.GetProperty("reason").GetString());
        Assert.Equal(new[] { "manifest.json", "started.json" },
            Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Select(Path.GetFileName).Order().ToArray());
    }

    [Fact]
    public void LaterFailureCannotOverwriteFirstCaptureOrItsReferences()
    {
        MetadataReference[] references = [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)];
        Assert.Equal("captured", EmitReferenceCapture.Capture(references, directory, new InvalidOperationException(), CancellationToken.None));
        var before = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);
        Assert.Equal("already-started", EmitReferenceCapture.Capture([], directory, new ArgumentException("later secret"), CancellationToken.None));
        Assert.Equal(before.Keys.Order(), Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, bytes) in before)
            Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    private JsonDocument ReadManifest() => JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));

    [Fact]
    public void ReadErrorAfterPartialChunkDoesNotRefundConsumedBudget()
    {
        using var input = new PartialReadThenFailureStream();
        long total = 0;
        Assert.Throws<IOException>(() => EmitReferenceCapture.ReadImage(input,
            new EmitReferenceCapture.CaptureLimits(MaxFileBytes: 8, MaxTotalBytes: 8),
            ref total, CancellationToken.None, out _));
        Assert.Equal(3, total);
    }

    [Fact]
    public void InvalidPeReadConsumesBudgetEvenThoughItCannotBeCaptured()
    {
        Directory.CreateDirectory(directory);
        var first = Path.Combine(directory, "invalid-first.dll");
        var second = Path.Combine(directory, "invalid-second.dll");
        File.WriteAllBytes(first, new byte[64]);
        File.WriteAllBytes(second, new byte[64]);
        using var metadata = AssemblyMetadata.CreateFromImage(File.ReadAllBytes(typeof(object).Assembly.Location));
        MetadataReference[] references = [metadata.GetReference(filePath: first), metadata.GetReference(filePath: second)];

        Assert.Equal("incomplete", EmitReferenceCapture.Capture(references, directory,
            "System.InvalidOperationException", CancellationToken.None,
            new EmitReferenceCapture.CaptureLimits(MaxFileBytes: 100, MaxTotalBytes: 100)));
        using var manifest = ReadManifest();
        Assert.False(manifest.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(64, manifest.RootElement.GetProperty("readBytes").GetInt64());
        var entries = manifest.RootElement.GetProperty("references").EnumerateArray().ToArray();
        Assert.Equal("error", entries[0].GetProperty("status").GetString());
        // No CLI metadata and malformed metadata have different PEReader exceptions;
        // both are incomplete captures, and neither may refund already-read bytes.
        Assert.Contains(entries[0].GetProperty("errorType").GetString(),
            new[] { "System.BadImageFormatException", "System.InvalidOperationException" });
        Assert.Equal("total-limit", entries[1].GetProperty("status").GetString());
        Assert.Empty(Directory.EnumerateFiles(directory, "*.pe"));
    }

    [Fact]
    public void FileLargerThanBudgetIsRefusedBeforeReading()
    {
        Assert.Equal("incomplete", EmitReferenceCapture.Capture(
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)], directory,
            "System.InvalidOperationException", CancellationToken.None,
            new EmitReferenceCapture.CaptureLimits(MaxFileBytes: 1, MaxTotalBytes: 1)));
        using var manifest = ReadManifest();
        Assert.Equal(0, manifest.RootElement.GetProperty("readBytes").GetInt64());
        Assert.Equal("file-too-large", manifest.RootElement.GetProperty("references")[0].GetProperty("status").GetString());
        Assert.Empty(Directory.EnumerateFiles(directory, "*.pe"));
    }

    [Fact]
    public void ExistingPeArtifactIsNeverOverwrittenOrAcceptedAsThisCapturesEvidence()
    {
        Directory.CreateDirectory(directory);
        var bytes = File.ReadAllBytes(typeof(object).Assembly.Location);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var existing = Path.Combine(directory, hash + ".pe");
        byte[] marker = [1, 2, 3];
        File.WriteAllBytes(existing, marker);

        Assert.Equal("incomplete", EmitReferenceCapture.Capture(
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)], directory,
            new InvalidOperationException(), CancellationToken.None));
        Assert.Equal(marker, File.ReadAllBytes(existing));
        using var manifest = ReadManifest();
        Assert.False(manifest.RootElement.GetProperty("complete").GetBoolean());
        Assert.NotEqual("captured", manifest.RootElement.GetProperty("references")[0].GetProperty("status").GetString());
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private sealed class PartialReadThenFailureStream : Stream
    {
        private bool suppliedChunk;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 8;
        public override long Position
        {
            get => suppliedChunk ? 3 : 0;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (suppliedChunk)
                throw new IOException("Input failed after delivering a partial chunk");
            Assert.True(count >= 3);
            buffer.AsSpan(offset, 3).Fill(1);
            suppliedChunk = true;
            return 3;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
