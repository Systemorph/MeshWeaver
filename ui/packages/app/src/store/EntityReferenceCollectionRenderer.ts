import { map, Observable, Subscription } from "rxjs";
import { Workspace } from "@open-smc/data/src/Workspace";
import { Collection } from "@open-smc/data/src/contract/Collection";
import { keys } from "lodash-es";
import { appStore } from "./appStore";
import { removeArea } from "./appReducer";
import { EntityReference } from "@open-smc/data/src/contract/EntityReference";
import { selectByReference } from "@open-smc/data/src/operators/selectByReference";
import { values } from "lodash";
import { renderControl } from "./renderControl";

export class EntityReferenceCollectionRenderer {
    subscription = new Subscription();
    private state: Record<string, Subscription> = {};

    constructor(
        protected areaReferences$: Observable<EntityReference[]>,
        protected collections: Workspace<Collection<Collection>>,
        protected dataContextWorkspace: Workspace
    ) {
        this.subscription.add(() => {
            values((this.state))
                .forEach(subscription => subscription.unsubscribe());
        })
    }

    renderAddedReferences() {
        this.subscription.add(
            this.areaReferences$
                .subscribe(
                    references => {
                        references?.filter(reference => !this.state[reference.id])
                            .forEach(reference => {
                                const control$ =
                                    this.collections.pipe(map(selectByReference(reference)));
                                this.state[reference.id] =
                                    renderControl(control$, this.collections, reference.id, this.dataContextWorkspace);
                            });
                    }
                )
        );
    }

    renderRemovedReferences() {
        this.subscription.add(
            this.areaReferences$
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