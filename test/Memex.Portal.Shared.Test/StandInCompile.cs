using System;
using System.IO;
using System.Linq;
using MeshWeaver.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// The ONE emit every in-memory stand-in assembly of this project goes through — and, when it
/// fails, the reading that says whether the failure is the test's or the process's.
///
/// <para>🚨 #5212 (originally #890): on Linux x64 CI the SHARED Roslyn of a test process sometimes
/// enters a bad state part-way through a run. From then on <c>Emit</c> throws a
/// <c>NullReferenceException</c> in <c>MetadataWriter.PopulateNestedClassTableRows</c>, extra
/// references become invisible (<c>CS0246</c>/<c>CS0234</c> for a stand-in passed right there in
/// the reference list), or a "successful" compile emits a DLL without its own type. Every one of
/// those used to read as a defect of the test that happened to compile next. Production already
/// carries the canary for this — <see cref="EmitPipeline.ProbeSharedEmitState"/> — so a failed
/// stand-in compile runs it and puts its <c>canary=…</c> verdict into the failure message, and the
/// next red names the defect itself.</para>
///
/// <para>A DIAGNOSTIC, never a recovery: the compile still fails exactly as before — no retry, no
/// second compiler, no fallback. Only the message grows. The probe runs only on the failing path,
/// and it never throws (every outcome is a string), so it cannot replace the original fault; the
/// original exception, when <c>Emit</c> threw, is kept as the inner exception.</para>
/// </summary>
internal static class StandInCompile
{
    /// <summary>The marker every failure message carries ahead of the canary verdict — what a
    /// reader (and <c>StandInCompileFailureNamesTheEmitCanaryTest</c>) keys on.</summary>
    public const string CanaryMarker = "EmitPipeline.ProbeSharedEmitState (#5212 / #890):";

    /// <summary>
    /// Emits <paramref name="compilation"/> to bytes, or throws a <see cref="StandInCompileFailedException"/>
    /// whose message carries the compile errors AND the shared-emit canary's verdict.
    /// </summary>
    /// <param name="compilation">The stand-in compilation, fully configured by the caller.</param>
    /// <returns>The emitted assembly image.</returns>
    public static byte[] Emit(CSharpCompilation compilation)
    {
        using var buffer = new MemoryStream();
        EmitResult result;
        try
        {
            result = compilation.Emit(buffer);
        }
        catch (Exception emitFault) when (emitFault is not OperationCanceledException)
        {
            throw Failed(compilation,
                $"Emit THREW {emitFault.GetType().FullName}: {emitFault.Message}", emitFault);
        }

        if (!result.Success)
            throw Failed(compilation, string.Join(Environment.NewLine,
                result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)), inner: null);

        return buffer.ToArray();
    }

    private static StandInCompileFailedException Failed(
        CSharpCompilation compilation, string what, Exception? inner)
    {
        var verdict = EmitPipeline.ProbeSharedEmitState(compilation);
        var message =
            $"Stand-in compile '{compilation.AssemblyName}' failed:{Environment.NewLine}{what}"
            + $"{Environment.NewLine}{CanaryMarker} {verdict}";

        // Also into the test's own output, so the verdict is next to the run's other lines even
        // where a runner truncates the failure message.
        TestContext.Current.TestOutputHelper?.WriteLine(message);
        return new StandInCompileFailedException(message, inner);
    }
}

/// <summary>A stand-in assembly could not be compiled; the message carries the
/// <see cref="EmitPipeline.ProbeSharedEmitState"/> verdict (#5212 / #890).</summary>
/// <param name="message">The compile errors (or the Emit fault) plus the canary verdict.</param>
/// <param name="inner">The exception <c>Emit</c> threw, if it threw.</param>
internal sealed class StandInCompileFailedException(string message, Exception? inner)
    : Exception(message, inner);
