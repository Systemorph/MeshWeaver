# MeshWeaver.Todo

## Overview
MeshWeaver.Todo is a sample task management application built on the MeshWeaver framework. It demonstrates reactive layout areas, data binding, CRUD operations, and content collections in a straightforward domain.

## Features
- Domain model: `TodoItem` with title, description, category, responsible person, due date, and status tracking
- Reference data: `TodoCategory` dimension and `TodoStatus` enum
- Layout areas: Summary, AllItems, TodosByCategory, Planning, MyTasks, Backlog, TodaysFocus
- Pre-loaded sample data via `TodoSampleData`
- Embedded content collection for thumbnails and static assets

## Usage
```csharp
configuration.ConfigureTodoApplication();
```

This registers data sources, layout areas, and content collections on a message hub.

## Related Projects
- [MeshWeaver.Layout](../../../src/MeshWeaver.Layout/README.md) -- Layout framework
- [MeshWeaver.Data](../../../src/MeshWeaver.Data/README.md) -- Data access framework
- [MeshWeaver.ContentCollections](../../../src/MeshWeaver.ContentCollections/) -- Content collection support
