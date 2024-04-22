import { bindingsToReferences, ControlRenderer } from "./ControlRenderer";
import { distinctUntilEqual } from "@open-smc/data/src/operators/distinctUntilEqual";
import { effect } from "@open-smc/utils/src/operators/effect";
import { syncWorkspaces } from "./syncWorkspaces";
import { sliceByReference } from "@open-smc/data/src/sliceByReference";
import { Workspace } from "@open-smc/data/src/Workspace";
import { map, Subscription } from "rxjs";
import { ItemTemplateControl } from "@open-smc/layout/src/contract/controls/ItemTemplateControl";
import { PathReference } from "@open-smc/data/src/contract/PathReference";
import { renderControl } from "./renderControl";
import { renderControlTo } from "./renderControlTo";
import { ControlModel } from "./appStore";
import { updateByReferenceActionCreator } from "@open-smc/data/src/workspaceReducer";
import { Collection } from "@open-smc/data/src/contract/Collection";
import { keys } from "lodash-es";

export class ItemTemplateRenderer extends ControlRenderer<ItemTemplateControl> {
    protected render() {
        const dataWorkspace = new Workspace<Collection>(null, "template");

        this.subscription.add(
            this.control$
                .pipe(map(control => control?.data))
                .pipe(distinctUntilEqual())
                .pipe(
                    effect(
                        data =>
                            syncWorkspaces(
                                sliceByReference(this.dataContextWorkspace, bindingsToReferences(data)),
                                dataWorkspace
                            )
                    )
                )
                .subscribe()
        );

        const view$ =
            this.control$
                .pipe(map(control => control?.view));

        const controlModelWorkspace = new Workspace<ControlModel>({
            componentTypeName: "LayoutStackControl",
            props: {}
        }, "model");

        this.subscription.add(
            dataWorkspace
                .pipe(distinctUntilEqual())
                .pipe(
                    effect(
                        data => {
                            const subscription = new Subscription();
                            const areas: string[] = [];

                            data && keys(data).forEach(id => {
                                const itemDataContext =
                                    sliceByReference(dataWorkspace, new PathReference(`/${id}`));

                                subscription.add(itemDataContext.subscription);

                                const area = `${this.area}/${id}`;

                                subscription.add(
                                    renderControl(view$, this.collections, area, itemDataContext)
                                );

                                areas.push(area);
                            });

                            controlModelWorkspace.next(updateByReferenceActionCreator({
                                reference: new PathReference("/props/areas"),
                                value: areas
                            }));

                            return subscription;
                        }
                    )
                )
                .subscribe()
        );

        this.subscription.add(
            renderControlTo(controlModelWorkspace, this.area)
        );
    }
}