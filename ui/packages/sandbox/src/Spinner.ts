import { ControlBase, ControlBuilderBase } from "./ControlBase";

export class Spinner extends ControlBase {
    constructor() {
        super("SpinnerControl");
    }
}

export class SpinnerBuilder extends ControlBuilderBase<Spinner> {
    constructor() {
        super(Spinner);
    }
}

export const makeSpinner = () => new SpinnerBuilder();