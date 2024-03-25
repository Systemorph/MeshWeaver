import { filter } from "rxjs";

export type Ctor<T> = new(...args: any[]) => T;

// TODO: deserialize properly from the very beginning and then rely only on instanceof (3/19/2024, akravets)
export const isOfType = <T>(obj: any, ctor: Ctor<T>): obj is T =>
    hasType(obj) && hasType(ctor)
        ? obj.$type === ctor.$type
        : obj instanceof ctor;

export const ofType = <T>(ctor: Ctor<T>) =>
    filter((value: unknown) => isOfType(value, ctor));

const hasType = (value: any): value is {$type: string} => value?.$type !== undefined;