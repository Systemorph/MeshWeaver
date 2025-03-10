# MeshWeaver.Activities

MeshWeaver.Activities implements the activity tracking framework defined in MeshWeaver.Activities.Contract. It provides concrete implementations for activity monitoring, logging, and sub-activity management.

## Overview

The library provides:
- Implementation of `IActivity` interface
- Activity lifecycle management
- Sub-activity tracking
- Integration with message hub system

## Implementation

### Activity Class
```csharp
public class Activity : IActivity
{
    private readonly IMessageHub _messageHub;
    private readonly IDictionary<string, ActivityLog> _subActivities;

    public Activity(string name, IMessageHub messageHub)
    {
        Name = name;
        _messageHub = messageHub;
    }

    public string Name { get; }

    public IActivity StartSubActivity(string name)
    {
        // Creates and tracks a new sub-activity
        return new Activity(name, _messageHub);
    }

    public void LogInformation(string message)
    {
        // Logs activity information through message hub
    }

    public async Task<ActivityLog> Complete(Action<ActivityLog> onComplete = null)
    {
        // Completes the activity and all sub-activities
        var log = new ActivityLog
        {
            Status = ActivityStatus.Succeeded,
            SubActivities = _subActivities
        };
        
        onComplete?.Invoke(log);
        return log;
    }
}
```

## Usage Examples

### Basic Activity Tracking
```csharp
// Create and use an activity
var activity = new Activity("MyActivity", messageHub);
activity.LogInformation("Processing started");
await activity.Complete();
```

### Hierarchical Activities
```csharp
// Main activity with sub-activities
var activity = new Activity("MainProcess", messageHub);
var subActivity1 = activity.StartSubActivity("SubProcess1");
var subActivity2 = activity.StartSubActivity("SubProcess2");

// Log information in different activities
activity.LogInformation("Main process running");
subActivity1.LogInformation("Sub-process 1 running");
subActivity2.LogInformation("Sub-process 2 running");

// Complete sub-activities
await subActivity1.Complete();
await subActivity2.Complete();

// Complete main activity
await activity.Complete(log => 
{
    // Access completion information
    var subActivities = log.SubActivities;
});
```

### Auto-Completion Support
```csharp
var activity = new Activity("AutoComplete", messageHub);
var subActivity = activity.StartSubActivity("SubProcess");

// Start completion of main activity
var completionTask = activity.Complete();

// Complete sub-activity - main activity will complete automatically
await subActivity.Complete();
```

## Features

1. **Activity Management**
   - Creation and tracking of activities
   - Hierarchical activity structure
   - Automatic sub-activity management

2. **Logging**
   - Activity status tracking
   - Information logging
   - Completion state management

3. **Message Hub Integration**
   - Activity events broadcasting
   - Message-based logging
   - Cross-component communication

## Related Projects

- [MeshWeaver.Activities.Contract](../MeshWeaver.Activities.Contract/README.md) - Core activity interfaces
- MeshWeaver.Import - Import functionality using activities
- MeshWeaver.Data - Data management with activity tracking
