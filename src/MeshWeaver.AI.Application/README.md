# MeshWeaver.AI.Application

This project contains the application layer for the MeshWeaver AI system, providing layout areas and UI components for managing AI agents.

## Components

### Application Setup
- **AgentsApplicationExtensions**: Configuration extensions for setting up the Agents application
- **AgentsApplicationNodeAttribute**: Mesh node attribute for registering the Agents application
- **ApplicationAddress**: Defines the application addresses (e.g., `Agents`)

### Layout Areas
- **AgentOverviewArea**: Provides the agent overview page with Mermaid diagrams and agent cards
- **AgentDetailsArea**: Provides detailed views for individual agents
- **AIExtensions**: Layout configuration extensions

## Usage

The application is automatically registered as a mesh node through the `AgentsApplicationNodeAttribute` and will be available at the `Agents` application address.

### Features
- Interactive Mermaid diagrams showing agent relationships
- Clickable agent cards with badges for default agents, exposed agents, and delegation capabilities
- Detailed agent views with instructions, attributes, and plugins
- Proper navigation between overview and details pages

## Dependencies
- MeshWeaver.AI (core AI interfaces and types)
- MeshWeaver.Layout (UI layout system)
- MeshWeaver.Mesh.Contract (mesh networking)
- MeshWeaver.Messaging (message hub infrastructure)
