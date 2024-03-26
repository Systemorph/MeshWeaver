import { filter } from "rxjs";
import { hasType } from "./hasType";

export type Ctor<T> = new(...args: any[]) => T;

/**
 @deprecated use instanceof instead (3/26/2024, akravets)
 */
export const isOfType = <T>(obj: any, ctor: Ctor<T>): obj is T =>
    hasType(obj) && hasType(ctor)
        ? obj.$type === ctor.$type
        : obj instanceof ctor;

export const ofType = <T>(ctor: Ctor<T>) =>
    filter((value: unknown) => isOfType(value, ctor));