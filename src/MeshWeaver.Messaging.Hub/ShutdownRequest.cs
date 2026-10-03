namespace MeshWeaver.Messaging;

[CanBeIgnored]
[SystemMessage]
[InfrastructureOnly]
internal record ShutdownRequest(MessageHubRunLevel RunLevel, long Version);
