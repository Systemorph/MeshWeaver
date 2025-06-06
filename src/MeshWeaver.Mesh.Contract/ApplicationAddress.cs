﻿using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Mesh;


public record ApplicationAddress(string Id) : Address(TypeName, Id)
{
    public const string TypeName = "app";
}
public record UiAddress(string Id = null) : Address(TypeName, Id ?? Guid.NewGuid().AsString())
{

    public const string TypeName = "ui";

}

public record SignalRAddress(string Id = null) : Address(TypeName, Id ?? Guid.NewGuid().AsString())
{
    public const string TypeName = "signalr";
}
public record KernelAddress(string Id = null) : Address(TypeName, Id ?? Guid.NewGuid().AsString())
{
    public const string TypeName = "kernel";
}
public record NotebookAddress(string Id = null) : Address(TypeName, Id ?? Guid.NewGuid().AsString())
{
    public const string TypeName = "nb";
}
public record ArticlesAddress(string Id) : Address(TypeName, Id)
{
    public const string TypeName = "articles";
}

public record PortalAddress(string Id = null) : Address(TypeName, Id ?? Guid.NewGuid().AsString())
{
    public const string TypeName = "portal";
}

