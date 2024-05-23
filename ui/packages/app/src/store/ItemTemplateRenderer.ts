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
import { appStore, LayoutAreaModel } from "./appStore";
import { Collection } from "@open-smc/data/src/contract/Collection";
import { keys } from "lodash-es";
import { Renderer } from "./Renderer";
import { RendererStackTrace } from "./RendererStackTrace";
import { UiControl } from "@open-smc/layout/src/contract/controls/UiControl";
import { removeAreaActionCreator } from "./appReducer";
import { values } from "lodash";
import { LayoutStackView } from "../controls/LayoutStackControl";

export class ItemTemplateRenderer extends ControlRenderer<ItemTemplateControl> {
    readonly data: Workspace<Collection>;
    readonly view$: Observable<UiControl>;
    private state: Record<string, ItemTemplateItemRenderer> = {};

    constructor(
        area: string,
        control$: Observable<ItemTemplateControl>,
        stackTrace: RendererStackTrace
    ) {
        super(area, control$, stackTrace);

        this.subscription.add(
            () =>
                values((this.state))
                    .forEach(itemRenderer => {
                        itemRenderer.subscription.unsubscribe();
                        appStore.dispatch(removeAreaActionCreator(itemRenderer.expandedArea));
                    })
        );

        this.data = new Workspace<Collection>(null);

        this.subscription.add(
            this.control$
                .pipe(map(control => control?.data))
                .pipe(distinctUntilEqual())
                .pipe(
                    effect(
                        data => {
                            const subscription = new Subscription();
                            const dataSlice = sliceByReference(this.dataContext, bindingsToReferences(data));
                            subscription.add(dataSlice.subscription);
                            subscription.add(syncWorkspaces(dataSlice, this.data));
                            return subscription;
                        }
                    )
                )
                .subscribe()
        );

        this.view$ =
            control$
                .pipe(map(control => control?.view));

        const areaModelWorkspace =
            new Workspace<LayoutAreaModel<LayoutStackView>>(
                {
                    area,
                    controlName: "LayoutStackControl",
                    props: {}
                }
            );

        this.subscription.add(
            this.data
                .pipe(
                    effect(
                        data => {
                            data && keys(data)
                                .filter(id => !this.state[id])
                                .forEach(id => {
                                    this.state[id] = new ItemTemplateItemRenderer(this, id, this.stackTrace);
                                });

                            const areas = data && keys(data).map(id => this.state[id].expandedArea);

                            areaModelWorkspace.update(state => {
                                state.props.areas = areas;
                            });

                            keys(this.state).forEach(id => {
                                if (!data?.[id]) {
                                    const itemRenderer = this.state[id];
                                    appStore.dispatch(removeAreaActionCreator(itemRenderer.expandedArea));
                                    itemRenderer.subscription.unsubscribe();
                                    delete this.state[id];
                                }
                            })
                        }
                    )
                )
                .subscribe()
        );

        this.subscription.add(
            this.renderControlTo(areaModelWorkspace)
        );
    }

    protected renderAreaModel() {
    }
}

export class ItemTemplateItemRenderer extends Renderer {
    readonly subscription = new Subscription();
    readonly area: string;
    readonly expandedArea: string;

    constructor(
        itemTemplateRenderer: ItemTemplateRenderer,
        itemId: string,
        stackTrace: RendererStackTrace
    ) {
        super(stackTrace);

        this.area = `${itemTemplateRenderer.area}/${itemId}`;

        this.dataContext = new Workspace(null);

        const dataContext = sliceByReference(
            itemTemplateRenderer.data,
            new PathReference(`/${itemId}`)
        );

        this.subscription.add(dataContext.subscription);
        this.subscription.add(syncWorkspaces(dataContext, this.dataContext));

        const { area, subscription } =
            renderControl(
                "",
                itemTemplateRenderer.view$,
                this.stackTrace
            );

        this.expandedArea = area;

        this.subscription.add(
            subscription
        );
    }
}