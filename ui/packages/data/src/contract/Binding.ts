import { type } from "@open-smc/serialization/src/type";

@type("OpenSmc.Layout.DataBinding.Binding")
export class Binding {
    /**
     * @param path JsonPath string
     */
    constructor(public path: string) {
    }
}

export type ValueOrBinding<T = unknown> = Binding |
    (T extends object ? {[TKey in keyof T]: ValueOrBinding<T[TKey]>} : T);

export const isBinding = (value: unknown): value is Binding => value instanceof Binding;