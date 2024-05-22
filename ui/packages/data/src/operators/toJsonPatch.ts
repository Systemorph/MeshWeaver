import { filter, map, Observable, pairwise } from "rxjs";
import {compare} from 'fast-json-patch';
import { JsonPatch } from "../contract/JsonPatch";
import { isEmpty } from "lodash-es";

export const toJsonPatch = <T>() =>
    (source: Observable<T>) =>
        source
            .pipe(pairwise())
            .pipe(
                map(([a, b]) => compare(a, b)),
                filter(operations => !isEmpty(operations)),
                map(operations => new JsonPatch(operations as any))
            );
