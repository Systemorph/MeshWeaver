import type { HtmlView } from "@open-smc/application/src/controls/HtmlControl";
import { ControlBase, ControlBuilderBase } from "./ControlBase";

export class Html extends ControlBase implements HtmlView {
    data: string;

    constructor() {
        super("HtmlControl");
    }
}

export class HtmlBuilder extends ControlBuilderBase<Html> {
    constructor(html?: string) {
        super(Html);
        this.withData(html);
    }
}

export const makeHtml = (html?: string) => new HtmlBuilder(html);