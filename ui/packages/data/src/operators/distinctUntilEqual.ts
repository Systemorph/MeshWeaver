import { distinctUntilChanged, Observable } from "rxjs";
import { isEqual } from "lodash-es";

export const distinctUntilEqual = () =>
    <T>(source: Observable<T>) =>
        source.pipe(distinctUntilChanged<T>(isEqual));