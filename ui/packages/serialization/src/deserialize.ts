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

                if (constructor) {
                    if (constructor.deserialize) {
                        return constructor.deserialize(props);
                    }

                    return assign(new constructor(), plainObjectDeserializer(props));
                }

                return plainObjectDeserializer(value);
            }
        }
    )
}

const plainObjectDeserializer = (props: {}) => mapValues(props, deserialize);

// function canDeserialize<T>(ctor: new (...args: any[]) => any): ctor is CanDeserialize<T> {
//     return (ctor as CanDeserialize<T>).deserialize !== undefined;
// }
//
// type CanDeserialize<T> = {
//     new (...args: any[]): T;
//     deserialize(props: T): T;
// }