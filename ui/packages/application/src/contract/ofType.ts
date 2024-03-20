import { filter } from "rxjs";

export type Ctor<T> = new(...args: any[]) => T;

// TODO: deserialize properly from the very beginning and then rely only on instanceof (3/19/2024, akravets)
export const isOfType = <T>(obj: any, ctor: Ctor<T>): obj is T =>
    obj?.$type ? obj.$type === (ctor as any).$type : obj instanceof ctor;

export const ofType = <T>(ctor: Ctor<T>) =>
    filter((value: unknown) => isOfType(value, ctor));