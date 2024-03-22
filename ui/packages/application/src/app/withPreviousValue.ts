import { pairwise, pipe, startWith } from "rxjs";

function withPreviousValue<T>() {
    return pipe(
        startWith(undefined),
        pairwise<T>()
    );
}