import { Observable, tap } from "rxjs";

export const log = (name?: string) =>
    (source: Observable<any>) =>
        source.pipe(tap(value => console.log(name, value)));