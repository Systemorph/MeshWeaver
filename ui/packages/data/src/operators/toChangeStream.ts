import { WorkspaceReference } from "../contract/WorkspaceReference";
import { filter, map, Observable, pairwise } from "rxjs";
import { DataChangedEvent } from "../contract/DataChangedEvent";
import { compare } from "fast-json-patch";
import { identity, isEmpty } from "lodash-es";
import { JsonPatch } from "../contract/JsonPatch";

export const toChangeStream = <T>(reference: WorkspaceReference) =>
    (source: Observable<T>): Observable<DataChangedEvent> =>
        source
            .pipe(pairwise())
            .pipe(
                map(([a, b]) => {
                    if (a === undefined) {
                        return new DataChangedEvent(reference, b, "Full")
                    }

                    const operations = compare(a, b);

                    if (!isEmpty(operations)) {
                        return new DataChangedEvent(reference, new JsonPatch(operations as any), "Patch")
                    }
                }),
                filter(identity)
            );