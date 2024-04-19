import { Observer, Subject, Subscription } from "rxjs";
import { UiControl } from "@open-smc/layout/src/contract/controls/UiControl";
import { Workspace } from "@open-smc/data/src/Workspace";
import { ControlModel } from "./appStore";
import { Collection } from "@open-smc/data/src/contract/Collection";
import { syncWorkspaces } from "./syncWorkspaces";
import { LayoutStackControl } from "@open-smc/layout/src/contract/controls/LayoutStackControl";
import { ControlRenderer } from "./ControlRenderer";
import { LayoutStackRenderer } from "./LayoutStackRenderer";
import ItemTemplateControl from "../controls/ItemTemplateControl";
import { ItemTemplateRenderer } from "./ItemTemplateRenderer";

export class ControlRendererDispatcher implements Observer<UiControl> {
    private control: UiControl;
    private subscription: Subscription;
    private subject = new Subject<UiControl>;

    constructor(
        private controlModelWorkspace: Workspace<ControlModel>,
        private collections: Workspace<Collection<Collection>>,
        private name?: string
    ) {
    }

    complete(): void {
    }

    error(err: any): void {
    }

    next(value: UiControl): void {
        if (value?.constructor !== this.control?.constructor) {
            this.control = value;
            this.subscription?.unsubscribe();

            if (value) {
                this.subscription = new Subscription();
                const renderer = this.getRenderer(value);
                this.subscription.add(renderer.subscription);
                this.subscription.add(
                    syncWorkspaces(
                        this.controlModelWorkspace,
                        renderer
                    )
                );
            }
        }
        this.subject.next(value);
    }

    private getRenderer(value: UiControl) {
        if (value instanceof LayoutStackControl) {
            return new LayoutStackRenderer(this.subject, this.collections, this.name);
        }

        if (value instanceof ItemTemplateControl) {
            return new ItemTemplateRenderer(this.subject, this.collections, this.name);
        }

        return new ControlRenderer(this.subject, this.collections, this.name);
    }
}