import { UiControl } from "@open-smc/layout/src/contract/controls/UiControl";
import { omit } from "lodash-es";
import { ValueOrReference } from "@open-smc/data/src/contract/ValueOrReference";
import { cloneDeepWith } from "lodash";
import { Binding, ValueOrBinding } from "@open-smc/data/src/contract/Binding";
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
import { renderControlTo } from "./renderControlTo";

export class ControlRenderer<T extends UiControl = UiControl> {
    subscription = new Subscription();
    protected dataContextWorkspace: Workspace;

    constructor(
        protected control$: Observable<T>,
        protected collections: Workspace<Collection<Collection>>,
        protected area: string,
        protected parentDataContextWorkspace?: Workspace
    ) {
        this.dataContextWorkspace = new Workspace(null, `${area}/dataContext`);

        this.subscription.add(
            this.control$
                .pipe(map(control => control?.dataContext))
                .pipe(distinctUntilChanged())
                .pipe(
                    effect(
                        dataContext =>
                            syncWorkspaces(
                                dataContext ? sliceByReference(this.collections, dataContext)
                                    : parentDataContextWorkspace,
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

                                const controlModelWorkspace =
                                    sliceByReference(this.dataContextWorkspace, controlModel);

                                return renderControlTo(controlModelWorkspace, this.area);
                            }
                        }
                    )
                )
                .subscribe()
        );
    }

    protected getModel(control: UiControl): ControlModel {
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

export const bindingsToReferences = <T>(props: ValueOrBinding<T>): ValueOrReference<T> =>
    cloneDeepWith(
        props,
        value =>
            value instanceof Binding
                ? new JsonPathReference(value.path) : undefined
    );

const nestedAreasToIds = <T>(props: T): T =>
    cloneDeepWith(
        props,
        value => value instanceof EntityReference
            ? value.id : undefined
    );