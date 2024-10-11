﻿namespace MeshWeaver.Data;

public record WorkspaceStoreReference : WorkspaceReference<EntityStore>
{
    public string Path => "$";

    public override string ToString() => Path;
}

public record WorkspaceStateReference : WorkspaceReference<EntityStore>
{
    public string Path => "$";

    public override string ToString() => Path;
}
