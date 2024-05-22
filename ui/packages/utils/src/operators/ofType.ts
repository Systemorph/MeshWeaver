import { filter } from "rxjs";

export const ofType = <T>(ctor: new (...args: any) => T) =>
    filter((value: unknown): value is T => value instanceof ctor);