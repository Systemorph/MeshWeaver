import { pairwise, pipe, startWith } from "rxjs";

export function withPreviousValue<T>() {
    return pipe(
        startWith(undefined),
        pairwise<T>()
    );
}