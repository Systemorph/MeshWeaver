---
Title: "Todo Unified References"
Abstract: >
  This document demonstrates all addressable items in the Todo application using MeshWeaver's
  unified content reference notation. It covers data entities, content files, and layout areas
  specific to the Todo domain.
Thumbnail: "images/UnifiedReferences.svg"
Published: "2025-12-06"
Authors:
  - "Roland Bürgi"
Tags:
  - "Todo"
  - "Data"
  - "Layout Areas"
---

This document showcases all addressable items in the Todo application.

## Data References

### TodoItem Collection

Display all todo items:

```
@data/app/Todo/TodoItem
```

@data/app/Todo/TodoItem

### Single TodoItem

Display a specific todo item by ID:

```
@data/app/Todo/TodoItem/1
```

@data/app/Todo/TodoItem/1

### TodoCategory Collection

Display all todo categories:

```
@data/app/Todo/TodoCategory
```

@data/app/Todo/TodoCategory

### Single TodoCategory

Display a specific category:

```
@data/app/Todo/TodoCategory/Work
```

@data/app/Todo/TodoCategory/Work

## Content References

### Embedding an Image

```
@content/app/Todo/Todo/images/todoapp.jpeg
```

@content/app/Todo/Todo/images/todoapp.jpeg

## Layout Area References

### Summary Dashboard

The main summary view showing todo statistics:

```
@app/Todo/Summary
```

@app/Todo/Summary

### Today's Focus

Tasks due today and overdue items:

```
@app/Todo/TodaysFocus
```

@app/Todo/TodaysFocus

### All Items

Complete list of all todo items with bulk actions:

```
@app/Todo/AllItems
```

@app/Todo/AllItems

### Todos by Category

Items grouped by category:

```
@app/Todo/TodosByCategory
```

@app/Todo/TodosByCategory

### My Tasks

Current user's assigned tasks:

```
@app/Todo/MyTasks
```

@app/Todo/MyTasks

### Planning

Team workload and assignment view:

```
@app/Todo/Planning
```

@app/Todo/Planning

### Backlog

Unassigned tasks ready for assignment:

```
@app/Todo/Backlog
```

@app/Todo/Backlog
