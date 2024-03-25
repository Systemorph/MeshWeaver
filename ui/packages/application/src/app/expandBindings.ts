import { Observable } from "rxjs";
import { Bindable } from "../dataBinding/resolveBinding";

export const expandBindings = <T extends {}>(props: PropsInput<T>, parentDataContext: unknown) =>
    (source: Observable<unknown>): Observable<T> =>
        new Observable(subscriber => {

        });

export type PropsInput<T> = {
    [key: string]: Bindable<T>;
}