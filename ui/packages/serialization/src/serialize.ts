import { cloneDeepWith, isFunction, mapValues } from "lodash-es";
import { Deserializable } from "./deserialize";

export type Serializable = {
    constructor: {
        $type: string;
    }
    serialize?: () => {}
}

export const isSerializable = (value: any): value is Serializable =>
    isFunction(value?.constructor) && value.constructor.$type !== undefined;

export const serialize =
    <T>(value: T): T | Deserializable<T> => {
        return cloneDeepWith(
            value,
            value => {
                if (isSerializable(value)) {
                    const {$type} = value.constructor;

                    if (value.serialize !== undefined) {
                        return value.serialize();
                    }

                    return {
                        $type,
                        ...mapValues(value, serialize)
                    }
                }
            }
        )
    }