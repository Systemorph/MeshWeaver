export abstract class WorkspaceReferenceBase<T = unknown> {
    abstract get(data: unknown): T;
}