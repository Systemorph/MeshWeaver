// Type forwarding for backwards compatibility
// AgentConfiguration is now defined in MeshWeaver.AI namespace
// This file ensures existing code using MeshWeaver.Graph.Configuration.AgentConfiguration continues to work

global using AgentConfiguration = MeshWeaver.AI.AgentConfiguration;
global using AgentDelegation = MeshWeaver.AI.AgentDelegation;

namespace MeshWeaver.Graph.Configuration;

// Types are now defined in MeshWeaver.AI namespace
// See: MeshWeaver.AI/AgentConfiguration.cs
