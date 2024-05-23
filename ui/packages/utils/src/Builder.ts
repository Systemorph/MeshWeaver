export type Writable<T> = { -readonly [P in keyof T]: T[P] }

export class Builder<T> {
    protected data: Partial<Writable<T>> = {};
}