using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace MeshWeaver.Fixture;

/// <summary>
/// Tracks message flow to identify infinite loops and hanging scenarios
/// </summary>
public static class MessageFlowTracker
{
    private static readonly ILogger Logger = new DebugFileLogger("MessageFlowTracker");
    private static readonly ConcurrentDictionary<string, MessageTrackingInfo> TrackedMessages = new();
    private static readonly object CleanupLock = new();
    private static DateTime LastCleanup = DateTime.Now;

    public static void TrackMessage(object message, string sourceAddress, string targetAddress, string operation)
    {
        try
        {
            var messageKey = GenerateMessageKey(message, sourceAddress, targetAddress);
            var trackingInfo = TrackedMessages.AddOrUpdate(messageKey,
                new MessageTrackingInfo(message, sourceAddress, targetAddress, operation),
                (key, existing) => existing.AddOperation(operation));

            Logger.LogInformation("MESSAGE_FLOW: {Operation} | {MessageType} | {Source} -> {Target} | Count: {Count} | Key: {Key}",
                operation, message?.GetType().Name ?? "null", sourceAddress, targetAddress, trackingInfo.OperationCount, messageKey);

            // Check for potential infinite loops
            if (trackingInfo.OperationCount > 10)
            {
                Logger.LogWarning("POTENTIAL_INFINITE_LOOP: Message {MessageType} between {Source} and {Target} has been processed {Count} times. Key: {Key}",
                    message?.GetType().Name ?? "null", sourceAddress, targetAddress, trackingInfo.OperationCount, messageKey);
            }

            if (trackingInfo.OperationCount > 50)
            {
                Logger.LogError("INFINITE_LOOP_DETECTED: Message {MessageType} between {Source} and {Target} has been processed {Count} times! Key: {Key}. Recent operations: {Operations}",
                    message?.GetType().Name ?? "null", sourceAddress, targetAddress, trackingInfo.OperationCount, messageKey,
                    string.Join(", ", trackingInfo.RecentOperations.TakeLast(10)));
            }

            // Periodic cleanup to prevent memory leaks
            PeriodicCleanup();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error tracking message flow");
        }
    }

    private static string GenerateMessageKey(object message, string sourceAddress, string targetAddress)
    {
        var messageType = message?.GetType().Name ?? "null";
        var messageHash = message?.GetHashCode().ToString() ?? "0";
        return $"{messageType}_{sourceAddress}_{targetAddress}_{messageHash}";
    }

    private static void PeriodicCleanup()
    {
        if (DateTime.Now - LastCleanup < TimeSpan.FromMinutes(1))
            return;

        lock (CleanupLock)
        {
            if (DateTime.Now - LastCleanup < TimeSpan.FromMinutes(1))
                return;

            var cutoff = DateTime.Now.AddMinutes(-5);
            var keysToRemove = TrackedMessages
                .Where(kvp => kvp.Value.FirstSeen < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                TrackedMessages.TryRemove(key, out _);
            }

            LastCleanup = DateTime.Now;
            Logger.LogDebug("Cleaned up {Count} old message tracking entries", keysToRemove.Count);
        }
    }

    public static void LogCurrentState()
    {
        Logger.LogInformation("CURRENT_MESSAGE_STATE: Tracking {Count} messages", TrackedMessages.Count);

        foreach (var kvp in TrackedMessages.Where(x => x.Value.OperationCount > 5))
        {
            Logger.LogWarning("HIGH_ACTIVITY_MESSAGE: Key: {Key}, Count: {Count}, Type: {Type}, Operations: {Operations}",
                kvp.Key, kvp.Value.OperationCount, kvp.Value.MessageType,
                string.Join(", ", kvp.Value.RecentOperations.TakeLast(5)));
        }
    }

    public static void Reset()
    {
        TrackedMessages.Clear();
        Logger.LogInformation("MESSAGE_TRACKING_RESET");
    }
}

public class MessageTrackingInfo
{
    public string MessageType { get; }
    public string SourceAddress { get; }
    public string TargetAddress { get; }
    public DateTime FirstSeen { get; }
    public int OperationCount { get; private set; }
    public List<string> RecentOperations { get; } = new();

    public MessageTrackingInfo(object message, string sourceAddress, string targetAddress, string operation)
    {
        MessageType = message?.GetType().Name ?? "null";
        SourceAddress = sourceAddress;
        TargetAddress = targetAddress;
        FirstSeen = DateTime.Now;
        OperationCount = 1;
        RecentOperations.Add($"{DateTime.Now:HH:mm:ss.fff}:{operation}");
    }

    public MessageTrackingInfo AddOperation(string operation)
    {
        OperationCount++;
        RecentOperations.Add($"{DateTime.Now:HH:mm:ss.fff}:{operation}");

        // Keep only recent operations to prevent memory bloat
        if (RecentOperations.Count > 20)
        {
            RecentOperations.RemoveAt(0);
        }

        return this;
    }
}
