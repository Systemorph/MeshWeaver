import { Observable, tap } from "rxjs";

export const log = (name?: string) =>
    <T>(source: Observable<T>) =>
        source.pipe(tap(value => void console.log(name, value)));