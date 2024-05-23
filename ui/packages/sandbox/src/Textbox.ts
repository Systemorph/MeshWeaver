import type { TextBoxView } from "@open-smc/application/src/controls/TextBoxControl";
import { ControlBase } from "./ControlBase";

export class Textbox extends ControlBase implements TextBoxView {
    constructor(public data: string) {
        super("TextboxControl");
    }
}