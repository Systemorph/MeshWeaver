namespace MeshWeaver.Messaging;

internal record ShutdownRequest(MessageHubRunLevel RunLevel, long Version);
