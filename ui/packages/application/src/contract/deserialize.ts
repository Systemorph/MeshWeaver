import { map, Observable } from "rxjs";
import { hasType } from "./hasType";
import { getConstructor } from "@open-smc/utils/src/contractMessage";
import { assign, cloneDeepWith } from "lodash-es";

export const deserialize = <T>() =>
    (source: Observable<T>) =>
        source
            .pipe(map(tryDeserialize));

const tryDeserialize = <T>(value: T): T => {
    return cloneDeepWith(
        value,
        value => {
            if (hasType(value)) {
                const {$type, ...props} = value;
                const constructor = getConstructor($type);
                const instance = constructor ? new constructor() : {};
                return assign(instance, tryDeserialize(props));
            }
        }
    )
}