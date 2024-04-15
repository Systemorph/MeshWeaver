export abstract class WorkspaceReference<T = unknown> {
    abstract get(data: unknown): T;
    abstract set(data: unknown, value: T): void;
}