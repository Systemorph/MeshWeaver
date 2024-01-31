import type { CheckboxView } from "@open-smc/application/controls/CheckboxControl";
import { ControlBase, ControlBuilderBase } from "./ControlBase";

export class Checkbox extends ControlBase implements CheckboxView {
    data: boolean;

    constructor() {
        super("CheckboxControl");
    }
}

export class CheckboxBuilder extends ControlBuilderBase<Checkbox> {
    constructor() {
        super(Checkbox);
    }
}

export const makeCheckbox = () => new CheckboxBuilder();