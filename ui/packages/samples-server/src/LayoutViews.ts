import { Collection } from "@open-smc/data/src/contract/Collection";
import { EntityReference } from "@open-smc/data/src/contract/EntityReference";
import { Workspace } from "@open-smc/data/src/Workspace";
import { distinctUntilEqual } from "@open-smc/data/src/operators/distinctUntilEqual";
import { UiControl } from "@open-smc/layout/src/contract/controls/UiControl.ts";
import { map, Observable, Subscription, tap } from "rxjs";
import { updateStore } from "@open-smc/data/src/workspaceReducer";

export const uiControlType = (UiControl as any).$type;

export class LayoutViews extends Workspace<Collection<UiControl>> {
    subscription = new Subscription();

    constructor() {
        super({});
    }
    
    addView(area = this.getAreaName(), control: UiControl) {
        this.update(state => {
            state[area] = control
        });

        return new EntityReference(uiControlType, area);
    }

    addViewStream(area = this.getAreaName(), control$: Observable<UiControl>) {
        this.subscription.add(
            control$
                .pipe(distinctUntilEqual())
                .pipe(
                    map(value => updateStore(state => {
                        state[area] = value;
                    }))
                )
                .subscribe(this)
        );
        return new EntityReference(uiControlType, area);
    }

    private getAreaName() {
        return `area_${this.n++}`;
    }

    private n = 0;
}