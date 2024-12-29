﻿using MeshWeaver.Messaging;

namespace MeshWeaver.Layout.Composition;

public record LayoutExecutionAddress() 
{
    public Guid Id { get; init; } = Guid.NewGuid();
}
