import type { TitleSize, TitleView } from "@open-smc/application/controls/TitleControl";
import { ControlBase } from "./ControlBase";

export class Title extends ControlBase implements TitleView {
    constructor(public data: string, public size: TitleSize = 3) {
        super("TitleControl");
    }
}