export interface MessageDelivery<TMessage = any> {
    readonly id?: string;
    readonly sender?: unknown;
    readonly target?: unknown;
    readonly message: TMessage;
    readonly properties?: Record<string, unknown>;
}