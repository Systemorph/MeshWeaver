﻿
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Application;

// domain name + env pair uniquely identifies the domain
// TODO V10: add "string ClientId" (05.06.2024, Alexander Kravets)
public record ApplicationAddress(string Name, string Environment)
{
    public override string ToString()
        => $"{Name}/{Environment}";
}
public record UiAddress 
{
    public string Id { get; init; } = Guid.NewGuid().AsString();

    public override string ToString()
        => $"ui_{Id}";
}

