// Type forwarding for backwards compatibility
// AgentConfiguration is now defined in MeshWeaver.Mesh namespace
// This file ensures existing code using MeshWeaver.Graph.Configuration.AgentConfiguration continues to work

global using AgentConfiguration = MeshWeaver.Mesh.AgentConfiguration;
global using AgentDelegation = MeshWeaver.Mesh.AgentDelegation;

namespace MeshWeaver.Graph.Configuration;

// Types are now defined in MeshWeaver.Mesh namespace
// See: MeshWeaver.Mesh.Contract/AgentConfiguration.cs
