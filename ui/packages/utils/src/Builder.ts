import { assign } from "lodash";

export type Writable<T> = { -readonly [P in keyof T]: T[P] }

export type Constructor<T = any> = { new(...args: any[]): T }

export class Builder<T> {
    instance: T;
    protected data: Partial<Writable<T>> = {};

    constructor(private ctor?: Constructor<T>) {
    }

    build() {
        this.instance = this.ctor ? new this.ctor() : {} as T;
        return assign(this.instance, this.data) as T;
    }
}