import type { TextboxView } from "@open-smc/application/src/controls/TextboxControl";
import { ControlBase } from "./ControlBase";

export class Textbox extends ControlBase implements TextboxView {
    constructor(public data: string) {
        super("TextboxControl");
    }
}