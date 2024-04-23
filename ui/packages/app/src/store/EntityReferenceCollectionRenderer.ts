import { map, Observable, Subscription } from "rxjs";
import { keys } from "lodash-es";
import { appStore } from "./appStore";
import { removeArea } from "./appReducer";
import { EntityReference } from "@open-smc/data/src/contract/EntityReference";
import { selectByReference } from "@open-smc/data/src/operators/selectByReference";
import { values } from "lodash";
import { renderControl } from "./renderControl";
import { Renderer } from "./Renderer";
import { RendererStackTrace } from "./RendererStackTrace";

export class EntityReferenceCollectionRenderer extends Renderer {
    readonly subscription = new Subscription();
    private state: Record<string, Subscription> = {};

    constructor(
        public readonly areaReferences$: Observable<EntityReference[]>,
        stackTrace: RendererStackTrace
    ) {
        super(null, stackTrace);

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
                                    this.rootContext.pipe(map(selectByReference(reference)));
                                this.state[reference.id] =
                                    renderControl(control$, reference.id, this.stackTrace);
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