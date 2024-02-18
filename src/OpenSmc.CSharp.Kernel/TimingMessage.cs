using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace OpenSmc.CSharp.Kernel;

public class TimingMessage : IDisposable
{
    private readonly Stopwatch stopWatch;
    private readonly ILogger logger;
    private readonly string title;
    public string Message { get; set; }

    public TimingMessage(ILogger logger, string title)
    {
        this.stopWatch = Stopwatch.StartNew();
        this.logger = logger;
        this.title = title;
    }

    public void Start() => stopWatch.Start();

    public void Stop() => stopWatch.Stop();

    void IDisposable.Dispose()
    {
        Stop();

        if (logger.IsEnabled(LogLevel.Trace))
        {
            var message = $"{this}. Elapsed: {stopWatch.Elapsed}";
            logger.LogDebug(message);
        }
    }

    public override string ToString()
    {
        var message = title;
        if (Message != null)
            message += ":" + Environment.NewLine + Message;
        return message;
    }
}

public static class TimingMessageLoggingExtensions
{
    public static TimingMessage LogTiming(this ILogger logger, string message)
    {
        return new TimingMessage(logger, message);
    }
}