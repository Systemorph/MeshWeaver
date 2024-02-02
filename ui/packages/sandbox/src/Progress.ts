import type { ProgressView } from "@open-smc/application/src/controls/ProgressControl";
import { ControlBase, ControlBuilderBase } from "./ControlBase";

export class Progress extends ControlBase implements ProgressView {
    progress: number;
    message: string;
    
    constructor() {
        super("ProgressControl");
    }
}

export class ProgressBuilder extends ControlBuilderBase<Progress> {
    constructor() {
        super(Progress);
    }

    withProgress(progress: number) {
        this.data.progress = progress;
        return this;
    }

    withMessage(message: string) {
        this.data.message = message;
        return this;
    }
}

export const makeProgress = () => new ProgressBuilder();