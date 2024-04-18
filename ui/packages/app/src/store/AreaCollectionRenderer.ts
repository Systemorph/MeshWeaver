import { Observable, Subscription } from "rxjs";
import { Workspace } from "@open-smc/data/src/Workspace";
import { Collection } from "@open-smc/data/src/contract/Collection";
import { keys } from "lodash-es";
import { appStore } from "./appStore";
import { removeArea } from "./appReducer";
import { AreaRenderer } from "./AreaRenderer";

export class AreaCollectionRenderer {
    subscription = new Subscription();
    private state: Record<string, AreaRenderer> = {};

    constructor(areas$: Observable<string[]>, collections: Workspace<Collection<Collection>>) {
        this.subscription.add(
            areas$
                .subscribe(
                    areas => {
                        areas?.filter(area => !this.state[area])
                            .forEach(area => {
                                this.state[area] =
                                    new AreaRenderer(area, collections);
                            });
                    }
                )
        );

        this.subscription.add(
            areas$
                .subscribe(
                    areas => {
                        keys(this.state).forEach(id => {
                            if (!areas?.find(area => area === id)) {
                                appStore.dispatch(removeArea(id));
                                this.state[id].subscription.unsubscribe();
                                delete this.state[id];
                            }
                        })
                    }
                )
        );
    }
}