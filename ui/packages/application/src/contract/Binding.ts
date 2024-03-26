import { contractMessage } from "@open-smc/utils/src/contractMessage";

@contractMessage("OpenSmc.Layout.DataBinding")
export class Binding {
    constructor(public path: string) {
    }
}