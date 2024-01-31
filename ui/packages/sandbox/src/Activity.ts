import type { ActivityView, UserInfo } from "@open-smc/application/controls/ActivityControl";
import { ControlBase, ControlBuilderBase } from "./ControlBase";

export class Activity extends ControlBase implements ActivityView {
    user?: UserInfo;
    date?: string;
    title?: string;
    color?: string;
    
    constructor() {
        super("ActivityControl");
    }
}

export class ActivityBuilder extends ControlBuilderBase<Activity> {
    constructor() {
        super(Activity);
    }

    withUser(user: UserInfo) {
        this.data.user = user;
        return this;
    }

    withDate(date: string) {
        this.data.date = date;
        return this;
    }

    withColor(color: string) {
        this.data.color = color;
        return this;
    }

    withTitle(value: string) {
        this.data.title = value;
        return this;
    }

}

export const makeActivity = () => new ActivityBuilder();