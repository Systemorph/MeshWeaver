#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FluentAssertions;
using MeshWeaver.Kernel.Hub;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Unit tests for the kernel's stdout-capture pipeline: <see cref="LoggerTextWriter"/>
/// (line-buffered TextWriter that flushes each completed line as a LogInformation
/// entry) and <see cref="CapturingTextWriter"/> (process-wide Console.Out hook that
/// routes to an AsyncLocal target). No mesh / no kernel â€” this is a pure check that
/// the building blocks behave as <c>KernelExecutor</c> expects.
/// </summary>
public class KernelStdoutCaptureTest
{
    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullLogger.Instance.BeginScope(state)!;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    [Fact]
    public void LoggerTextWriter_Buffers_Until_Newline()
    {
        var logger = new CapturingLogger();
        using var writer = new LoggerTextWriter(logger);

        writer.Write("Hel");
        writer.Write("lo, ");
        writer.Write("World");
        logger.Messages.Should().BeEmpty("no newline yet â€” should be buffered");

        writer.Write('\n');
        logger.Messages.Should().ContainSingle().Which.Should().Contain("Hello, World");
    }

    [Fact]
    public void LoggerTextWriter_Splits_On_Each_Newline()
    {
        var logger = new CapturingLogger();
        using var writer = new LoggerTextWriter(logger);

        writer.Write("line one\nline two\nline three\n");

        logger.Messages.Should().HaveCount(3);
        logger.Messages[0].Should().Contain("line one");
        logger.Messages[1].Should().Contain("line two");
        logger.Messages[2].Should().Contain("line three");
    }

    [Fact]
    public void LoggerTextWriter_Strips_CarriageReturn()
    {
        var logger = new CapturingLogger();
        using var writer = new LoggerTextWriter(logger);

        writer.Write("hello\r\n");

        logger.Messages.Should().ContainSingle().Which.Should().Be("hello");
    }

    [Fact]
    public void LoggerTextWriter_WriteLine_Flushes()
    {
        var logger = new CapturingLogger();
        using var writer = new LoggerTextWriter(logger);

        writer.WriteLine("immediate");

        logger.Messages.Should().ContainSingle().Which.Should().Contain("immediate");
    }

    [Fact]
    public void LoggerTextWriter_Dispose_Flushes_Pending_Buffer()
    {
        var logger = new CapturingLogger();
        var writer = new LoggerTextWriter(logger);

        writer.Write("partial line no newline");
        writer.Dispose();

        logger.Messages.Should().ContainSingle().Which.Should().Contain("partial line");
    }

    [Fact]
    public async Task CapturingTextWriter_AsyncLocal_Isolates_Concurrent_Captures()
    {
        // Two concurrent "scripts" each capture into their own writer. Output
        // from one must not leak into the other â€” the AsyncLocal target is what
        // makes this safe across thread-pool continuations. A naive global swap
        // of Console.Out would mix the two streams.
        var captureA = new StringWriter();
        var captureB = new StringWriter();

        async Task RunCaptureAsync(StringWriter into, string label, int delayMs)
        {
            using (CapturingTextWriter.Capture(into))
            {
                Console.Write($"{label}-1");
                await Task.Delay(delayMs);
                Console.Write($"{label}-2");
                await Task.Delay(delayMs);
                Console.Write($"{label}-3");
            }
        }

        await Task.WhenAll(
            RunCaptureAsync(captureA, "A", 30),
            RunCaptureAsync(captureB, "B", 50));

        captureA.ToString().Should().Be("A-1A-2A-3", "writer A must only see A's writes");
        captureB.ToString().Should().Be("B-1B-2B-3", "writer B must only see B's writes");
    }

    [Fact]
    public void CapturingTextWriter_Outside_Scope_Falls_Back_To_Original()
    {
        // Without an active capture, writes should pass through to the
        // installed fallback (the real Console.Out for the test runner).
        // We can't easily assert on the runner's Console, but we can verify
        // that a Capture scope's restorer puts the AsyncLocal back to null.
        var capture = new StringWriter();
        using (CapturingTextWriter.Capture(capture))
        {
            Console.Write("inside");
        }
        // After dispose: writes go to the real Console (and not to `capture`).
        var beforeLength = capture.ToString().Length;
        Console.Write("outside");
        capture.ToString().Length.Should().Be(beforeLength,
            "post-scope writes must not land in the no-longer-active capture");
    }
}
