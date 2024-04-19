import { UiControl } from "@open-smc/layout/src/contract/controls/UiControl";
import { omit } from "lodash-es";
import { ValueOrReference } from "@open-smc/data/src/contract/ValueOrReference";
import { cloneDeepWith } from "lodash";
import { Binding } from "@open-smc/data/src/contract/Binding";
import { JsonPathReference } from "@open-smc/data/src/contract/JsonPathReference";
import { EntityReference } from "@open-smc/data/src/contract/EntityReference";
import { Workspace } from "@open-smc/data/src/Workspace";
import { ControlModel } from "./appStore";
import { distinctUntilChanged, map, Observable, Subscription } from "rxjs";
import { Collection } from "@open-smc/data/src/contract/Collection";
import { effect } from "@open-smc/utils/src/operators/effect";
import { syncWorkspaces } from "./syncWorkspaces";
import { sliceByReference } from "@open-smc/data/src/sliceByReference";
import { distinctUntilEqual } from "@open-smc/data/src/operators/distinctUntilEqual";
import { LayoutStackControl } from "@open-smc/layout/src/contract/controls/LayoutStackControl";

export class ControlRenderer extends Workspace<ControlModel> {
    subscription = new Subscription();
    protected dataContextWorkspace: Workspace;

    constructor(
        protected control$: Observable<UiControl>,
        protected collections: Workspace<Collection<Collection>>,
        name?: string
    ) {
        super(null);

        this.dataContextWorkspace = new Workspace(null, name && `${name}/dataContext`);

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

        this.render();
    }

    protected render() {
        this.subscription.add(
            this.control$
                .pipe(distinctUntilEqual())
                .pipe(
                    effect(
                        control => {
                            if (control) {
                                const controlModel =
                                    this.getModel(control);

                                return syncWorkspaces(
                                    sliceByReference(this.dataContextWorkspace, controlModel),
                                    this
                                );
                            }
                        }
                    )
                )
                .subscribe()
        );
    }

    protected getModel(control: UiControl) {
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
    }
}

const extractProps = (control: UiControl) =>
    omit(control, 'dataContext');

type UiControlProps = ReturnType<typeof extractProps>;

export const bindingsToReferences = (props: UiControlProps): ValueOrReference =>
    cloneDeepWith(
        props,
        value =>
            value instanceof Binding
                ? new JsonPathReference(value.path) : undefined
    );

export const nestedAreas = (control: UiControl) => {
    if (control instanceof LayoutStackControl) {
        return control.areas;
    }
}

const nestedAreasToIds = <T>(props: T): T =>
    cloneDeepWith(
        props,
        value => value instanceof EntityReference
            ? value.id : undefined
    );