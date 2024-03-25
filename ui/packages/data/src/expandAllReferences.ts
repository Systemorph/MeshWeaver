import { Observable, switchMap } from "rxjs";
import { DataInput, WorkspaceReference } from "./data.contract";
import { cloneDeepWith } from "lodash-es";
import { isOfType } from "@open-smc/application/src/contract/ofType";
import { expandReference } from "./expandReference";

export const expandAllReferences = <T>(data: DataInput<T>) =>
    (source: Observable<unknown>): Observable<T> =>
        source.pipe(
            switchMap(
                value => {
                    const result = cloneDeepWith(
                        value,
                        v => isOfType(v, WorkspaceReference) ? "" : undefined
                    );

                    if (isOfType(value, WorkspaceReference)) {
                        return source.pipe(expandReference(value));
                    }
                }
            )
        );