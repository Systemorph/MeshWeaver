# MeshWeaver Todo Module

A complete Todo management module for MeshWeaver that demonstrates data modeling, mesh integration, and reactive UI layout areas.

## Features

### 📋 Data Model
- **TodoItem Entity**: Complete todo item with title, description, category, due date, and status
- **TodoStatus Enum**: Four states (Pending, InProgress, Completed, Cancelled)
- **Sample Data**: 10 diverse sample todos for testing and demonstration

### 🌐 Mesh Integration
- **Mesh Node Registration**: Automatic registration via `[assembly: TodoApplication]` attribute
- **Data Sources**: Configured with TodoItem entity persistence
- **Application Address**: Exposed at `new ApplicationAddress("Todo")`

### 🎯 Layout Areas
Five reactive layout areas that subscribe to todo item streams:

1. **TodoList**: Main todo list ordered by due date (read-only)
   - Status grouping (Pending → In Progress → Completed → Cancelled)
   - Due date warnings (⚠️ OVERDUE, 📅 DUE TODAY)
   - Status icons (⏳, 🔄, ✅, ❌)
   - Links to interactive areas

2. **TodosByCategory**: Todos grouped by category (read-only)
   - Category-based organization
   - Due date ordering within categories
   - Category badges and progress indicators

3. **TodoSummary**: Statistics and progress overview (read-only)
   - Total counts by status
   - Progress bars for completion rates
   - Overdue and due today alerts
   - Visual progress indicators

4. **AddTodo**: Interactive todo creation form
   - Form fields for title, description, category, due date
   - Real-time validation
   - DataChangeRequest submission
   - Success confirmation with live count

5. **TodoListWithActions**: Interactive todo list with action buttons
   - All todos with status-appropriate action buttons
   - Start/Complete/Reopen/Delete actions
   - Real-time status updates via DataChangeRequest
   - Optimistic UI updates

## Interactive Functionality

### ✅ Add New Todos

Navigate to `/app/Todo/AddTodo` to access the interactive todo creation form:

- **Title**: Required field for the todo item
- **Description**: Optional detailed description
- **Category**: Categorize your todo (defaults to "General")
- **Due Date**: Optional deadline for the todo
- **Status**: Automatically set to "Pending" for new items

The form uses MeshWeaver's Edit control with real-time validation and submits data via `DataChangeRequest`:

```csharp
var changeRequest = new DataChangeRequest()
    .WithCreations(todoToAdd);
await actionContext.Host.Hub.AwaitResponse(changeRequest);
```

### 🔄 Todo Actions

Navigate to `/app/Todo/TodoListWithActions` for the interactive todo management interface:

**Status Transition Actions:**
- **Pending** → Start, Complete
- **In Progress** → Complete, Back to Pending  
- **Completed** → Reopen
- **All Status** → Delete

Each action immediately updates the todo status via `DataChangeRequest`:

```csharp
var updatedTodo = todo with 
{ 
    Status = TodoStatus.Completed,
    UpdatedAt = DateTime.UtcNow
};
await SubmitTodoUpdate(host, updatedTodo);
```

### 🔄 Real-time Updates

All changes are immediately reflected across all layout areas:
- ✅ **Optimistic Updates**: UI responds instantly to user actions
- 🔄 **Data Synchronization**: Changes propagated via reactive streams
- 📊 **Live Statistics**: Summary counts update automatically
- 🎯 **Status Transitions**: Visual feedback for state changes

## Project Structure

```
samples/Todo/MeshWeaver.Todo/
├── MeshWeaver.Todo.csproj          # Project file with dependencies
├── TodoItem.cs                     # Main entity model (record type)
├── TodoStatus.cs                   # Status enumeration
├── TodoApplicationAttribute.cs     # Mesh node registration
├── TodoApplicationExtensions.cs    # Hub configuration
├── TodoViews.cs                    # Basic UI views
├── TodoLayoutArea.cs              # Reactive read-only layout areas
├── TodoManagement.cs              # Interactive management areas with forms and actions
├── TodoSampleData.cs              # Sample data provider
└── README.md                       # This documentation
```

## Dependencies

- **MeshWeaver.Mesh.Contract**: Core mesh functionality
- **MeshWeaver.Messaging.Hub**: Message hub for communication
- **MeshWeaver.Data**: Entity persistence and data sources
- **MeshWeaver.Layout**: Layout areas and UI composition
- **System.Reactive.Linq**: Observable streams for reactivity

### Key Components

- **TodoApplicationAttribute**: MeshNodeAttribute that registers the Todo application in the mesh
- **TodoApplicationExtensions**: Configuration extensions for the Todo hub
- **TodoViews**: UI views for the Todo application

### Usage

The Todo application is automatically registered in the mesh through the assembly-level attribute:

```csharp
[assembly: TodoApplication]
```

This allows the Todo application to be discovered and instantiated by the MeshWeaver hosting infrastructure.

## Build and Run

To build the project:

```bash
cd samples/Todo/MeshWeaver.Todo
dotnet build
```

The project is included in the main MeshWeaver solution and will be built as part of the overall solution build process.

## Future Enhancements

Potential areas for expansion:

- User assignment and collaboration features
- Task dependencies and hierarchies
- Time tracking and reporting
- Integration with external calendar systems
- Mobile and offline synchronization
- Advanced filtering and search capabilities
