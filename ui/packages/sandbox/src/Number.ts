import type { NumberView } from "@open-smc/application/controls/NumberControl";
import { ControlBase } from "./ControlBase";

export class Number extends ControlBase implements NumberView {
    constructor(public data: number) {
        super("NumberControl");
    }
}