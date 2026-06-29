namespace MeshWeaver.Messaging;

[CanBeIgnored]
[SystemMessage]
internal record ShutdownRequest(MessageHubRunLevel RunLevel, long Version);
