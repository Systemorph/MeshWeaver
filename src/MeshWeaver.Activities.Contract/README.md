# MeshWeaver.Activities.Contract

MeshWeaver.Activities.Contract defines the core interfaces, models, and contracts for the Activities framework. This library serves as the API contract between activity implementations and consumers.

## Overview

The library provides:
- Core activity interfaces
- Activity status models
- Progress tracking contracts
- Error handling definitions

## Core Concepts

### Activity
The base unit of work that can be monitored and logged:
```csharp
public interface IActivity
{
    string Name { get; }
    Task<ActivityLog> Complete(Action<ActivityLog> onComplete = null);
    IActivity StartSubActivity(string name);
    void LogInformation(string message);
}
```

### ActivityLog
Records the state and results of an activity:
```csharp
public record ActivityLog
{
    public ActivityStatus Status { get; init; }
    public string Message { get; init; }
    public IDictionary<string, ActivityLog> SubActivities { get; init; }
}
```

### ActivityStatus
```csharp
public enum ActivityStatus
{
    NotStarted,
    InProgress,
    Succeeded,
    Failed,
    Cancelled
}
```

## Usage Examples

### Basic Activity
```csharp
// Create a new activity
var activity = new Activity("MyActivity", messageHub);

// Log information during execution
activity.LogInformation("Processing started");

// Complete the activity
await activity.Complete(log => 
{
    // Log will have Succeeded status
    // Can perform final checks or logging
});
```

### Sub-Activities
```csharp
// Create main activity
var activity = new Activity("MyActivity", messageHub);

// Create and use a sub-activity
var subActivity = activity.StartSubActivity("SubProcess");
subActivity.LogInformation("Sub-process running");

// Complete sub-activity first
await subActivity.Complete();

// Then complete main activity
await activity.Complete(log => 
{
    // Main activity log will include sub-activity logs
    log.SubActivities.Should().HaveCount(1);
    log.SubActivities.First().Value.Status.Should().Be(ActivityStatus.Succeeded);
});
```

### Auto-Completion
Activities support automatic completion of sub-activities:
```csharp
var activity = new Activity("MyActivity", messageHub);
var subActivity = activity.StartSubActivity("SubProcess");

// Start completion of main activity
var completionTask = activity.Complete();

// Complete sub-activity
await subActivity.Complete();

// Main activity completes automatically after sub-activity
await activity.Complete(log => 
{
    log.Status.Should().Be(ActivityStatus.Succeeded);
    log.SubActivities.Should().HaveCount(1);
});
```

## Integration

### With Message Hub
Activities are designed to work with the MeshWeaver message hub system.
```

## Related Projects

- [MeshWeaver.Activities](../MeshWeaver.Activities/README.md) - Implementation of the activity framework
- MeshWeaver.Import - Import functionality using activity contracts
- MeshWeaver.Data - Data management with activity tracking
