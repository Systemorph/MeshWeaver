﻿using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Activities;

public record ActivityAddress(string Id = null) : Address(TypeName, Id ?? Guid.NewGuid().AsString())
{
    public const string TypeName = "activity";
}
