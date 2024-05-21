import { UiControl } from "@open-smc/layout/src/contract/controls/UiControl";
import { cloneDeepWith, omit } from "lodash-es";
import { ValueOrReference } from "@open-smc/data/src/contract/ValueOrReference";
import { Binding, ValueOrBinding } from "@open-smc/data/src/contract/Binding";
import { JsonPathReference } from "@open-smc/data/src/contract/JsonPathReference";
import { Workspace } from "@open-smc/data/src/Workspace";
import { app$, appStore, LayoutAreaModel } from "./appStore";
import { distinctUntilChanged, map, Observable, pipe, Subscription, take } from "rxjs";
import { effect } from "@open-smc/utils/src/operators/effect";
import { syncWorkspaces } from "./syncWorkspaces";
import { sliceByReference } from "@open-smc/data/src/sliceByReference";
import { distinctUntilEqual } from "@open-smc/data/src/operators/distinctUntilEqual";
import { setArea } from "./appReducer";
import { pathToUpdateAction } from "@open-smc/data/src/operators/pathToUpdateAction";
import { Renderer } from "./Renderer";
import { RendererStackTrace } from "./RendererStackTrace";
import { expandArea } from "./renderControl";

export class ControlRenderer<T extends UiControl = UiControl> extends Renderer {
    readonly subscription = new Subscription();
    readonly namespace: string;
    public expandedArea: string;

    constructor(
        public readonly area: string,
        public readonly control$: Observable<T>,
        stackTrace: RendererStackTrace
    ) {
        super(stackTrace);

        this.expandedArea = expandArea(area, this.stackTrace);

        this.dataContext = new Workspace(null);

        this.subscription.add(
            this.control$
                .pipe(map(control => control?.dataContext))
                .pipe(distinctUntilEqual())
                .pipe(
                    effect(
                        dataContext => {
                            if (dataContext) {
                                const subscription = new Subscription();
                                const dataContextSlice = sliceByReference(this.rootContext, dataContext);
                                subscription.add(dataContextSlice.subscription);
                                subscription.add(syncWorkspaces(dataContextSlice, this.dataContext));
                                return subscription;
                            }
                            else {
                                return syncWorkspaces(this.parentContext, this.dataContext);
                            }
                        }
                    )
                )
                .subscribe()
        );

        this.renderAreaModel();
    }

    protected renderAreaModel() {
        this.subscription.add(
            this.control$
                .pipe(
                    effect(
                        control => {
                            if (control) {
                                const areaModel =
                                    this.getAreaModel(control);

                                const subscription = new Subscription();

                                const areaModelWorkspace =
                                    sliceByReference(this.dataContext, areaModel);

                                subscription.add(areaModelWorkspace.subscription);
                                subscription.add(this.renderControlTo(areaModelWorkspace))

                                return subscription;
                            }
                        }
                    )
                )
                .subscribe()
        );
    }

    protected getAreaModel(control: T) {
        const controlName = control.constructor.name;
        const props = bindingsToReferences(
            extractProps(control)
        );

        return {
            area: this.expandedArea,
            controlName,
            props
        }
    }

    protected renderControlTo(areaModelWorkspace: Workspace<LayoutAreaModel>) {
        const subscription = new Subscription();

        subscription.add(
            areaModelWorkspace
                .pipe(distinctUntilEqual())
                .subscribe(layoutAreaModel => {
                    appStore.dispatch(setArea(layoutAreaModel))
                })
        );

        subscription.add(
            app$
                .pipe(map(appState => appState.areas[this.area]))
                .pipe(distinctUntilChanged())
                .pipe(map(pathToUpdateAction("")))
                .subscribe(areaModelWorkspace)
        );

        return subscription;
    }
}

export const extractProps = (control: UiControl) =>
    omit(control, 'dataContext');

export const bindingsToReferences = <T>(props: ValueOrBinding<T>): ValueOrReference<T> =>
    cloneDeepWith(
        props,
        value =>
            value instanceof Binding
                ? new JsonPathReference(value.path) : undefined
    );
