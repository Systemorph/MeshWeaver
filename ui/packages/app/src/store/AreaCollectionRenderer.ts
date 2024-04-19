import { map, Observable, Subscription } from "rxjs";
import { Workspace } from "@open-smc/data/src/Workspace";
import { Collection } from "@open-smc/data/src/contract/Collection";
import { keys } from "lodash-es";
import { appStore } from "./appStore";
import { removeArea } from "./appReducer";
import { ControlModelWorkspace } from "./ControlModelWorkspace";
import { EntityReference } from "@open-smc/data/src/contract/EntityReference";
import { selectByReference } from "@open-smc/data/src/operators/selectByReference";
import { renderControlTo } from "./renderControlTo";

// TODO: replace with function renderAreaCollection (4/19/2024, akravets)
export class AreaCollectionRenderer {
    subscription = new Subscription();
    private state: Record<string, Subscription> = {};

    constructor(areaReferences$: Observable<EntityReference[]>, collections: Workspace<Collection<Collection>>) {
        this.subscription.add(
            areaReferences$
                .subscribe(
                    references => {
                        references?.filter(reference => !this.state[reference.id])
                            .forEach(reference => {
                                const control$ = collections.pipe(map(selectByReference(reference)));
                                this.state[reference.id] =
                                    renderControlTo(new ControlModelWorkspace(control$, collections, reference.id), reference.id);
                            });
                    }
                )
        );

        this.subscription.add(
            areaReferences$
                .subscribe(
                    references => {
                        keys(this.state).forEach(id => {
                            if (!references?.find(reference => reference.id === id)) {
                                appStore.dispatch(removeArea(id));
                                this.state[id].unsubscribe();
                                delete this.state[id];
                            }
                        })
                    }
                )
        );
    }
}