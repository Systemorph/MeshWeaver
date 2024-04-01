import { type } from "@open-smc/serialization/src/type";

@type("OpenSmc.Layout.DataBinding.Binding")
export class Binding {
    /**
     * @param path JsonPath string
     */
    constructor(public path: string) {
    }
}

export type Bindable<T> = T | Binding;

export const isBinding = (value: unknown): value is Binding => value instanceof Binding;