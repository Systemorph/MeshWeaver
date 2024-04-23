import { bindingsToReferences, ControlRenderer } from "./ControlRenderer";
import { distinctUntilEqual } from "@open-smc/data/src/operators/distinctUntilEqual";
import { effect } from "@open-smc/utils/src/operators/effect";
import { syncWorkspaces } from "./syncWorkspaces";
import { sliceByReference } from "@open-smc/data/src/sliceByReference";
import { Workspace } from "@open-smc/data/src/Workspace";
import { map, Observable, Subscription } from "rxjs";
import { ItemTemplateControl } from "@open-smc/layout/src/contract/controls/ItemTemplateControl";
import { PathReference } from "@open-smc/data/src/contract/PathReference";
import { renderControl } from "./renderControl";
import { ControlModel } from "./appStore";
import { updateByReferenceActionCreator } from "@open-smc/data/src/workspaceReducer";
import { Collection } from "@open-smc/data/src/contract/Collection";
import { keys } from "lodash-es";
import { WorkspaceSlice } from "@open-smc/data/src/WorkspaceSlice";
import { Renderer } from "./Renderer";
import { RendererStackTrace } from "./RendererStackTrace";
import { qualifyArea } from "./qualifyArea";
import { UiControl } from "@open-smc/layout/src/contract/controls/UiControl";

export class ItemTemplateRenderer extends ControlRenderer<ItemTemplateControl> {
    readonly data: Workspace<Collection>;
    readonly view$: Observable<UiControl>;

    constructor(control$: Observable<ItemTemplateControl>, area: string, stackTrace: RendererStackTrace) {
        super(control$, area, stackTrace);

        this.data = new Workspace<Collection>(null, `${this.area}/data`);

        this.subscription.add(
            this.control$
                .pipe(map(control => control?.data))
                .pipe(distinctUntilEqual())
                .pipe(
                    effect(
                        data =>
                            syncWorkspaces(
                                sliceByReference(this.dataContext, bindingsToReferences(data)),
                                this.data
                            )
                    )
                )
                .subscribe()
        );

        this.view$ =
            control$
                .pipe(map(control => control?.view));

        const controlModelWorkspace = new Workspace<ControlModel>({
            componentTypeName: "LayoutStackControl",
            props: {}
        });

        this.subscription.add(
            this.data
                .pipe(distinctUntilEqual())
                .pipe(
                    effect(
                        data => {
                            const subscription = new Subscription();
                            const areas: string[] = [];

                            data && keys(data).forEach(id => {
                                const itemRenderer =
                                    new ItemTemplateItemRenderer(this, id, this.stackTrace);

                                subscription.add(itemRenderer.subscription);

                                areas.push(qualifyArea(itemRenderer.area, itemRenderer.stackTrace));
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
            this.renderControlTo(controlModelWorkspace)
        );
    }

    protected render() {
    }
}

export class ItemTemplateItemRenderer extends Renderer {
    readonly subscription = new Subscription();
    readonly area: string;

    constructor(
        itemTemplateRenderer: ItemTemplateRenderer,
        itemId: string,
        stackTrace: RendererStackTrace
    ) {
        super(
            sliceByReference(
                itemTemplateRenderer.data, new PathReference(`/${itemId}`),
                `${itemTemplateRenderer.area}/${itemId}`
            ),
            stackTrace
        );

        this.subscription.add(
            (this.dataContext as WorkspaceSlice).subscription
        );

        this.area = `${itemTemplateRenderer.area}/${itemId}`;

        this.subscription.add(
            renderControl(itemTemplateRenderer.view$, this.area, this.stackTrace)
        );
    }
}