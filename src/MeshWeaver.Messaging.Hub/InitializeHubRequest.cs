namespace MeshWeaver.Messaging;

/// <summary>
/// Request to initialize a message hub during startup.
/// Used to defer messages until initialization is complete.
/// </summary>
internal record InitializeHubRequest;
