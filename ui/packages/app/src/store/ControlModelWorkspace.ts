import { Observable, Subscription } from "rxjs";
import { UiControl } from "@open-smc/layout/src/contract/controls/UiControl";
import { Workspace } from "@open-smc/data/src/Workspace";
import { Collection } from "@open-smc/data/src/contract/Collection";
import { ControlModel } from "./appStore";
import { distinctUntilEqual } from "@open-smc/data/src/operators/distinctUntilEqual";
import { ControlRendererDispatcher } from "./ControlRendererDispatcher";

export class ControlModelWorkspace extends Workspace<ControlModel> {
    readonly subscription = new Subscription();

    constructor(private control$: Observable<UiControl>, private collections: Workspace<Collection<Collection>>, name?: string) {
        super(null, `${name}/model`);

        this.subscription.add(
            this.control$
                .pipe(distinctUntilEqual())
                .subscribe(new ControlRendererDispatcher(this, collections, name))
        );
    }
}