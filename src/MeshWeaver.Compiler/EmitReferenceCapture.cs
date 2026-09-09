using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace MeshWeaver.Compiler;

/// <summary>Opt-in CI evidence writer. Scheduling and cancellation belong to the caller.</summary>
internal static class EmitReferenceCapture
{
    internal sealed record CaptureLimits(
        int MaxReferences = 1024,
        long MaxFileBytes = 64L * 1024 * 1024,
        long MaxTotalBytes = 256L * 1024 * 1024);

    internal static string? GetCiDirectory(Func<string, string?> readEnvironment)
    {
        try
        {
            if (readEnvironment("GITHUB_ACTIONS") != "true" ||
                readEnvironment("MW_CAPTURE_EMIT_REFERENCES") != "1") return null;
            var root = readEnvironment("RUNNER_TEMP");
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || root.Contains('\0'))
                return null;
            return Path.Combine(Path.GetFullPath(root), "straggler-logs", "emit-reference-capture",
                $"process-{Environment.ProcessId}");
        }
        catch { return null; }
    }

    internal static string Capture(IReadOnlyList<MetadataReference> references, string directory,
        Exception failure, CancellationToken ct)
        => Capture(references, directory, failure.GetType().FullName ?? failure.GetType().Name, ct);

    internal static string Capture(IReadOnlyList<MetadataReference> references, string directory,
        string failureType, CancellationToken ct)
        => Capture(references, directory, failureType, ct, new CaptureLimits());

    internal static string Capture(IReadOnlyList<MetadataReference> references, string directory,
        string failureType, CancellationToken ct, string captureId)
        => Capture(references, directory, failureType, ct, new CaptureLimits(), captureId);

    internal static string Capture(IReadOnlyList<MetadataReference> references, string directory,
        string failureType, CancellationToken ct, CaptureLimits limits, string? captureId = null)
    {
        try
        {
            Directory.CreateDirectory(directory);
            try
            {
                using var sentinel = new FileStream(Path.Combine(directory, "started.json"),
                    FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                JsonSerializer.Serialize(sentinel, new { complete = false, reason = "started", captureId });
            }
            catch (IOException) when (File.Exists(Path.Combine(directory, "started.json")))
            { return "already-started"; }

            var entries = new List<Dictionary<string, object?>>();
            var writtenHashes = new HashSet<string>(StringComparer.Ordinal);
            long total = 0;
            var reason = references.Count > limits.MaxReferences ? "reference-limit" : "complete";
            for (var ordinal = 0; ordinal < Math.Min(references.Count, limits.MaxReferences); ordinal++)
            {
                var reference = references[ordinal];
                var entry = new Dictionary<string, object?>
                {
                    ["ordinal"] = ordinal, ["referenceType"] = reference.GetType().FullName,
                    ["kind"] = reference.Properties.Kind.ToString(),
                    ["aliases"] = reference.Properties.Aliases.ToArray(),
                    ["embedInteropTypes"] = reference.Properties.EmbedInteropTypes,
                    ["path"] = (reference as PortableExecutableReference)?.FilePath,
                    ["status"] = "error"
                };
                entries.Add(entry);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    CaptureReference(reference, entry, directory, writtenHashes, limits, ref total, ct);
                }
                catch (OperationCanceledException)
                { entry["status"] = "cancelled"; reason = "cancelled"; break; }
                catch (FileNotFoundException) { entry["status"] = "missing-file"; }
                catch (DirectoryNotFoundException) { entry["status"] = "missing-file"; }
                catch (Exception ex) { entry["errorType"] = ex.GetType().FullName; }
                if (!Equals(entry["status"], "captured") && reason == "complete") reason = "partial";
            }
            if (ct.IsCancellationRequested) reason = "cancelled";
            var complete = reason == "complete" && entries.Count == references.Count;
            var manifest = new
            {
                complete, reason, captureId, referenceCount = references.Count, readBytes = total,
                failureType,
                runtime = new
                {
                    framework = RuntimeInformation.FrameworkDescription,
                    version = Environment.Version.ToString(),
                    architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    os = RuntimeInformation.OSDescription,
                    processId = Environment.ProcessId,
                    managedThreadId = Environment.CurrentManagedThreadId,
                    processorCount = Environment.ProcessorCount,
                    compiler = Identity(typeof(CSharpCompilation).Assembly),
                    coreLib = Identity(typeof(object).Assembly),
                    jitFlags = JitFlags()
                },
                references = entries,
                limitation = "File bytes are reread after failure; matching MVIDs do not prove byte identity with held metadata. Object caches and process history are not captured."
            };
            var temporary = Path.Combine(directory, "manifest.tmp");
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(output, manifest);
            File.Move(temporary, Path.Combine(directory, "manifest.json"));
            return complete ? "captured" : "incomplete";
        }
        catch { return "incomplete"; }
    }

    private static void CaptureReference(MetadataReference reference, Dictionary<string, object?> entry,
        string directory, HashSet<string> writtenHashes, CaptureLimits limits,
        ref long total, CancellationToken ct)
    {
        if (reference is not PortableExecutableReference pe || string.IsNullOrEmpty(pe.FilePath))
        { entry["status"] = "unsupported-reference"; return; }
        // GetMetadata returns a copy; Roslyn documents that this copy need not be disposed.
        var metadata = pe.GetMetadata();
        var heldMvids = metadata switch
        {
            AssemblyMetadata assembly => assembly.GetModules().Select(Mvid).ToArray(),
            ModuleMetadata module => new[] { Mvid(module) },
            _ => Array.Empty<string>()
        };
        entry["metadataMvids"] = heldMvids;
        using var input = new FileStream(pe.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        entry["size"] = input.Length;
        var image = ReadImage(input, limits, ref total, ct, out var refusal);
        if (image is null) { entry["status"] = refusal; return; }
        entry["size"] = image.LongLength;
        var hash = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
        entry["sha256"] = hash;
        using var bytes = new MemoryStream(image, writable: false);
        using var reader = new PEReader(bytes, PEStreamOptions.LeaveOpen);
        var md = reader.GetMetadataReader();
        var fileMvids = new[] { md.GetGuid(md.GetModuleDefinition().Mvid).ToString("D") };
        entry["fileMvids"] = fileMvids;
        if (md.IsAssembly)
        {
            var definition = md.GetAssemblyDefinition();
            var name = new AssemblyName { Name = md.GetString(definition.Name), Version = definition.Version,
                CultureName = definition.Culture.IsNil ? null : md.GetString(definition.Culture) };
            if (!definition.PublicKey.IsNil) name.SetPublicKey(md.GetBlobBytes(definition.PublicKey));
            entry["fileAssemblyIdentity"] = name.FullName;
        }
        var imagePath = Path.Combine(directory, hash + ".pe");
        if (!writtenHashes.Contains(hash))
        {
            // A preexisting file is not evidence from this capture. Never reread it outside
            // the budget or accept an interrupted write under its content-addressed name.
            using var output = new FileStream(imagePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            const int chunkSize = 64 * 1024;
            for (var offset = 0; offset < image.Length; offset += chunkSize)
            {
                ct.ThrowIfCancellationRequested();
                output.Write(image, offset, Math.Min(chunkSize, image.Length - offset));
            }
            output.Flush();
            writtenHashes.Add(hash);
        }
        entry["file"] = Path.GetFileName(imagePath);
        entry["status"] = heldMvids.SequenceEqual(fileMvids) ? "captured" : "metadata-mismatch";
    }

    // The actual file-reading leaf, separated only to exercise partial I/O failures with
    // ordinary Streams. The caller owns input and retains the charged count on exceptions.
    internal static byte[]? ReadImage(Stream input, CaptureLimits limits, ref long total,
        CancellationToken ct, out string? refusal)
    {
        refusal = null;
        ct.ThrowIfCancellationRequested();
        var initialLength = input.Length;
        var initialPosition = input.Position;
        if (initialPosition < 0 || initialPosition > initialLength)
        { refusal = "incomplete-read"; return null; }
        var expectedBytes = initialLength - initialPosition;
        if (expectedBytes > limits.MaxFileBytes) { refusal = "file-too-large"; return null; }
        if (expectedBytes > limits.MaxTotalBytes - total) { refusal = "total-limit"; return null; }
        using var bytes = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var fileRemaining = limits.MaxFileBytes - bytes.Length;
            var totalRemaining = limits.MaxTotalBytes - total;
            var available = Math.Min(fileRemaining, totalRemaining);
            if (available <= 0)
            {
                if (input.Position == input.Length) break;
                refusal = fileRemaining <= 0 ? "file-too-large" : "total-limit";
                return null;
            }
            var count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, available));
            if (count == 0) break;
            // Charge immediately: cancellation, growth or a later error must not refund reads.
            total += count;
            bytes.Write(buffer, 0, count);
        }
        ct.ThrowIfCancellationRequested();
        // EOF alone is not proof of a complete image: a shared file may shrink while
        // being read, including at the exact budget boundary above. Preserve the read
        // charge, but never let a shortened image become successful capture evidence.
        if (bytes.Length != expectedBytes || input.Position != initialLength || input.Length != initialLength)
        { refusal = "incomplete-read"; return null; }
        return bytes.ToArray();
    }

    private static string Mvid(ModuleMetadata module)
    {
        var reader = module.GetMetadataReader();
        return reader.GetGuid(reader.GetModuleDefinition().Mvid).ToString("D");
    }

    private static object Identity(Assembly assembly) => new
    {
        name = assembly.GetName().FullName,
        informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        mvid = assembly.ManifestModule.ModuleVersionId.ToString("D")
    };

    private static Dictionary<string, string?> JitFlags()
    {
        var result = new Dictionary<string, string?>();
        foreach (var name in new[] { "DOTNET_TieredPGO", "DOTNET_TieredCompilation", "DOTNET_ReadyToRun",
                     "COMPlus_TieredPGO", "COMPlus_TieredCompilation", "COMPlus_ReadyToRun" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            result[name] = value is null or "0" or "1" ? value : "other";
        }
        return result;
    }
}
