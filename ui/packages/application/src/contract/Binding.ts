import { contractMessage } from "@open-smc/utils/src/contractMessage";

@contractMessage("OpenSmc.Layout.DataBinding")
export class Binding {
    /**
     * @param path JsonPath string
     */
    constructor(public path: string) {
    }
}

export type Bindable<T> = T | Binding;

export const isBinding = (value: unknown): value is Binding => value instanceof Binding;