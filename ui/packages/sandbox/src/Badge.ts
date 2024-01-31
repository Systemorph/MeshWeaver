import type { BadgeView } from "@open-smc/application/controls/BadgeControl";
import { ControlBase, ControlBuilderBase } from "./ControlBase";

export class Badge extends ControlBase implements BadgeView {
    title: string;
    subTitle?: string;
    color?: string;

    constructor() {
        super("BadgeControl");
    }
}

export class BadgeBuilder extends ControlBuilderBase<Badge> {
    constructor() {
        super(Badge);
    }

    withTitle(title: string) {
        this.data.title = title;
        return this;
    }

    withSubtitle(subtitle: string) {
        this.data.subTitle = subtitle;
        return this;
    }

    withColor(color: string) {
        this.data.color = color;
        return this;
    }
}

export const makeBadge = () => new BadgeBuilder();