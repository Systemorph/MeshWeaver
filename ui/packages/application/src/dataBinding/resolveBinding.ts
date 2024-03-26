import { contractMessage } from "@open-smc/utils/src/contractMessage";
import { isOfType } from "../contract/ofType";

export type Bindable<T> = T | Binding;

export function isBinding(data: unknown) {
    return isOfType(data, Binding);
}

export function makeBinding(path: string) {
    return new Binding(path);
}