import {
    createContext,
    useContext,
    useEffect,
    useMemo, useState
} from "react";
import { createStore, Store } from "@open-smc/store/store";
import { flatten, keyBy, keys, map, mapValues, pick, values, without } from "lodash";
import { Category, Named, SelectionByCategory } from "../../application.contract";
import { useCategoryApi } from "../../useCategoryApi";
import { ClassificationInner } from "./ClassificationInner";
import { insertAfter } from "@open-smc/utils/insertAfter";
import { arrayMove } from "@dnd-kit/sortable";
import { insertBefore } from "@open-smc/utils/insertBefore";
import { makeUseSelector } from "@open-smc/store/useSelector";

type ClassificationContext = {
    readonly store: Store<ClassificationState>;
}

interface Dragging {
    readonly element: string;
    readonly fromCategory?: string;
    readonly overElement?: string;
    readonly overCategory?: string;
}

export interface ClassificationState {
    readonly selectedElements: SelectedElements;
    readonly elements: Record<string, Named>;
    readonly classificationCategories: Category[];
    readonly dragging?: Dragging;
}

export type DroppableType = "Element" | "Category" | "AllElements";

type SelectedElements = Record<string, string[]>;

const context = createContext<ClassificationContext>(null);

function useClassificationContext() {
    return useContext(context);
}

export function useClassificationStore() {
    const {store} = useClassificationContext();
    return store;
}

export const useClassificationSelector = makeUseSelector(useClassificationStore);

interface ClassificationProps {
    data: SelectionByCategory;
    elementsCategory: Category;
    classificationCategories: Category[];
    onChange: (data: SelectionByCategory) => void;
}

export const getSelectedElements = (model: SelectionByCategory) => mapValues(model, els => map(els, 'systemName'))

export function Classification({data, elementsCategory, classificationCategories, onChange}: ClassificationProps) {
    const {sendCategoryRequest} = useCategoryApi();
    const [store, setStore] = useState<Store<ClassificationState>>();

    useEffect(() => {
        const selectedElements = data ? getSelectedElements(data) : {};
        const elements = data ? keyBy(flatten(values(data)), 'systemName') : {};

        const store = createStore({
            selectedElements,
            elements,
            classificationCategories
        } as ClassificationState);

        sendCategoryRequest(elementsCategory.category).then(elements => {
            store.setState(state => {
                state.elements = keyBy(elements, 'systemName');
            });
        });

        setStore(store);
    }, [data, elementsCategory, classificationCategories]);

    useEffect(() => {
        return store?.subscribe("selectedElements", () => {
            const {
                selectedElements,
                elements,
            } = store.getState();

            const data = mapValues(
                selectedElements,
                els => map(els, el => elements[el])
            );

            for (const key in data) {
                if (data[key].length === 0) {
                    delete data[key];
                }
            }

            onChange?.(data);
        });
    }, [store, onChange]);

    const value = useMemo(() => ({store}), [store]);

    if (!store) {
        return null;
    }

    return (
        <context.Provider value={value}>
            <ClassificationInner/>
        </context.Provider>
    );
}

export function useSelectedElements() {
    const selectedElements = useClassificationSelector('selectedElements');
    return useMemo(() => flatten(values(selectedElements)), [selectedElements]);
}

export function useSelectElement() {
    const {setState, getState} = useClassificationStore();
    const {sendCategoryChange} = useCategoryApi();

    return (toCategory: string, element: string, overElement: string) => {
        const fromCategory = findCategory(getState().selectedElements, element);

        const {selectedElements} = setState(state => {
            const {selectedElements} = state;

            const elements = selectedElements[toCategory];

            if (fromCategory === toCategory) {
                const oldIndex = elements.indexOf(element);
                const newIndex = elements.indexOf(overElement);

                state.selectedElements = {
                    ...selectedElements,
                    [toCategory]: arrayMove(elements, oldIndex, newIndex)
                };
            }
            else {
                state.selectedElements = {
                    ...selectedElements,
                    [toCategory]: elements ? insertBefore(elements, element, overElement)
                        : [element]
                };

                if (fromCategory) {
                    state.selectedElements[fromCategory] = without(selectedElements[fromCategory], element);
                }
            }
        });

        const selection = pick(selectedElements, toCategory);

        if (fromCategory) {
            selection[fromCategory] = selectedElements[fromCategory] ?? null;
        }

        sendCategoryChange(selection);
    }
}

export function useUnselectElement() {
    const {setState, getState} = useClassificationStore();
    const {sendCategoryChange} = useCategoryApi();

    return (element: string) => {
        const category = findCategory(getState().selectedElements, element);

        const {selectedElements} = setState(({selectedElements}) => {
            selectedElements[category] = without(selectedElements[category], element);
        });

        sendCategoryChange(pick(selectedElements, category));
    }
}

export function useUnselectAll() {
    const {setState} = useClassificationStore();
    const {sendCategoryChange} = useCategoryApi();

    return () => {
        const {classificationCategories} = setState(state => {
            state.selectedElements = {};
        });
        sendCategoryChange(
            classificationCategories
                .map(c => c.category)
                .reduce((prev, cur) => ({...prev, [cur]: null}), {})
        );
    }
}

export function findCategory(selectedElements: Record<string, string[]>, element: string) {
    return keys(selectedElements).find(key => selectedElements[key].includes(element));
}