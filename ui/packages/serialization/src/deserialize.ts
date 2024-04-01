import { assign, cloneDeepWith, mapValues } from "lodash-es";
import { getConstructor } from "./type";

export type Deserializable<T = unknown> = T & {
    $type: string;
}

export const isDeserializable = (value: any): value is Deserializable => value?.$type !== undefined;

export const deserialize = <T = unknown>(value: T | Deserializable<T>): T => {
    return cloneDeepWith(
        value,
        value => {
            if (isDeserializable(value)) {
                const {$type, ...props} = value;
                const constructor = getConstructor($type);
                const instance = constructor ? new constructor() : {};
                return assign(instance, mapValues(props, deserialize));
            }
        }
    )
}