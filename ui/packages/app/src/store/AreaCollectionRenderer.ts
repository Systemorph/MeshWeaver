import { map, Observable, Subscription } from "rxjs";
import { keys } from "lodash-es";
import { appStore } from "./appStore";
import { removeArea } from "./appReducer";
import { EntityReference } from "@open-smc/data/src/contract/EntityReference";
import { selectByReference } from "@open-smc/data/src/operators/selectByReference";
import { values } from "lodash";
import { renderControl, RenderingResult } from "./renderControl";
import { Renderer } from "./Renderer";
import { RendererStackTrace } from "./RendererStackTrace";

export class AreaCollectionRenderer extends Renderer {
    readonly subscription = new Subscription();
    private state: Record<string, RenderingResult> = {};
    areas: string[] = [];

    constructor(
        protected readonly areaReferences$: Observable<EntityReference[]>,
        stackTrace: RendererStackTrace
    ) {
        super(stackTrace);

        this.subscription.add(() => {
            values(this.state)
                .forEach(result => {
                    result.subscription.unsubscribe();
                    appStore.dispatch(removeArea(result.area));
                });
        })
    }

    renderNewAreas() {
        this.subscription.add(
            this.areaReferences$
                .subscribe(
                    references => {
                        references?.filter(reference => !this.state[reference.id])
                            .forEach(reference => {
                                const control$ =
                                    this.rootContext.pipe(map(selectByReference(reference)));
                                this.state[reference.id] =
                                    renderControl(reference.id, control$, this.stackTrace);
                            });

                        this.areas = references?.map(reference => this.state[reference.id].area) ?? [];
                    }
                )
        );
    }

    cleanupRemovedAreas() {
        this.subscription.add(
            this.areaReferences$
                .subscribe(
                    references => {
                        keys(this.state).forEach(id => {
                            if (!references?.find(reference => reference.id === id)) {
                                const { area, subscription } = this.state[id];
                                appStore.dispatch(removeArea(area));
                                subscription.unsubscribe();
                                delete this.state[id];
                            }
                        })
                    }
                )
        );
    }
}