namespace MeshWeaver.Messaging;

[CanBeIgnored]
internal record ShutdownRequest(MessageHubRunLevel RunLevel, long Version);
