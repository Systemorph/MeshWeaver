---
Name: Concepts
Category: Documentation
Description: Core patterns and principles for building reactive UIs in MeshWeaver
Icon: /static/storage/content/MeshWeaver/GUI/Concepts/icon.svg
---

# GUI Concepts

This section covers the fundamental patterns and principles that underpin MeshWeaver's reactive UI system.

## Topics

### View Patterns

- [WithView Patterns](MeshWeaver/GUI/Concepts/WithView) - Understanding the different `WithView` overloads for adding controls to containers

### Reactivity

- [Static vs Dynamic Views](MeshWeaver/GUI/Concepts/Observables) - Understanding when and how UI areas update in response to data changes

### Data Flow

- [Data Binding](MeshWeaver/GUI/Concepts/DataBinding) - How data flows through the UI with DataContext and UpdatePointer

### Configuration

- [Property Attributes](MeshWeaver/GUI/Concepts/Attributes) - Attributes for forms, validation, and control customization

## Key Principles

| Principle | Description |
|-----------|-------------|
| **Immutable Controls** | Controls are records - each `With*` method returns a new instance |
| **Static Structure** | Container structure is static; only area content can be dynamic |
| **Observable Updates** | Use observables to create reactive, updating UI areas |
| **Declarative** | Define what to render, not how to update it |
