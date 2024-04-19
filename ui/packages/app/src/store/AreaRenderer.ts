import { distinctUntilChanged, map, Observable, Subscription } from "rxjs";
import { UiControl } from "@open-smc/layout/src/contract/controls/UiControl";
import { selectByPath } from "@open-smc/data/src/operators/selectByPath";
import { Workspace } from "@open-smc/data/src/Workspace";
import { Collection } from "@open-smc/data/src/contract/Collection";
import { effect } from "@open-smc/utils/src/operators/effect";
import { pathToUpdateAction } from "@open-smc/data/src/operators/pathToUpdateAction";
import { setArea } from "./appReducer";
import { omit } from "lodash-es";
import { LayoutStackControl } from "@open-smc/layout/src/contract/controls/LayoutStackControl";
import { cloneDeepWith } from "lodash";
import { EntityReference } from "@open-smc/data/src/contract/EntityReference";
import { app$, appStore, ControlModel } from "./appStore";
import { distinctUntilEqual } from "@open-smc/data/src/operators/distinctUntilEqual";
import { Binding } from "@open-smc/data/src/contract/Binding";
import { JsonPathReference } from "@open-smc/data/src/contract/JsonPathReference";
import { ValueOrReference } from "@open-smc/data/src/contract/ValueOrReference";
import { AreaCollectionRenderer } from "./AreaCollectionRenderer";
import { sliceByReference } from "@open-smc/data/src/sliceByReference";

const uiControlType = (UiControl as any).$type;

export class AreaRenderer {
    readonly subscription = new Subscription();
    private control$: Observable<UiControl>;
    private dataContextWorkspace: Workspace;

    constructor(public area: string, private collections: Workspace<Collection<Collection>>) {
        this.control$ =
            collections
                .pipe(map(selectByPath(`/${uiControlType}/${area}`)));

        this.initDataContextWorkspace();

        const controlModelWorkspace =
            new Workspace<ControlModel>(null, `${area}/model`);

        this.subscription.add(
            this.control$
                .pipe(map(controlToModel))
                .pipe(distinctUntilEqual())
                .pipe(
                    effect(
                        controlModel => {
                            if (controlModel) {
                                return syncWorkspaces(
                                    sliceByReference(this.dataContextWorkspace, controlModel),
                                    controlModelWorkspace
                                );
                            }
                        }
                    )
                )
                .subscribe()
        );

        this.subscription.add(
            app$
                .pipe(map(appState => appState.areas[area]?.control))
                .pipe(distinctUntilChanged())
                .pipe(map(pathToUpdateAction("")))
                .subscribe(controlModelWorkspace)
        );

        this.subscription.add(
            controlModelWorkspace
                .subscribe(control => {
                    appStore.dispatch(setArea({
                        area,
                        control
                    }))
                })
        );

        const nestedAreas$ =
            this.control$.pipe(map(nestedAreas));

        const nestedAreasRenderer =
            new AreaCollectionRenderer(nestedAreas$, collections);

        this.subscription.add(nestedAreasRenderer.subscription);
    }

    private initDataContextWorkspace() {
        this.dataContextWorkspace = new Workspace(null, `${this.area}/dataContext`);

        this.subscription.add(
            this.control$
                .pipe(map(control => control?.dataContext))
                .pipe(distinctUntilChanged())
                .pipe(
                    effect(
                        dataContext =>
                            syncWorkspaces(
                                sliceByReference(this.collections, dataContext),
                                this.dataContextWorkspace
                            )
                    )
                )
                .subscribe()
        );
    }
}

function syncWorkspaces(workspace1: Workspace, workspace2: Workspace) {
    const subscription = new Subscription();

    subscription.add(
        workspace1
            .pipe(distinctUntilEqual())
            .pipe(map(pathToUpdateAction("")))
            .subscribe(workspace2)
    );

    subscription.add(
        workspace2
            .pipe(distinctUntilEqual())
            .pipe(map(pathToUpdateAction("")))
            .subscribe(workspace1)
    );

    return subscription;
}

const controlToModel = (control: UiControl) => {
    if (control) {
        const componentTypeName = control.constructor.name;
        const props = bindingsToReferences(
            nestedAreasToIds(
                extractProps(control)
            )
        );

        return {
            componentTypeName,
            props
        }
    }
};

const extractProps = (control: UiControl) =>
    omit(control, 'dataContext');

type UiControlProps = ReturnType<typeof extractProps>;

const bindingsToReferences = (props: UiControlProps): ValueOrReference =>
    cloneDeepWith(
        props,
        value =>
            value instanceof Binding
                ? new JsonPathReference(value.path) : undefined
    );

const nestedAreas = (control: UiControl) => {
    if (control instanceof LayoutStackControl) {
        return control.areas?.map(area => area.id);
    }
}

const nestedAreasToIds = <T>(props: T): T =>
    cloneDeepWith(
        props,
        value => value instanceof EntityReference
            ? value.id : undefined
    );