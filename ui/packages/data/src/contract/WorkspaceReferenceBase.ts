export abstract class WorkspaceReferenceBase<T = unknown> {
    abstract get(data: unknown): T;
    abstract set(data: unknown, value: T): void;
}