import { createContext, useContext, useEffect, useState } from "react";
import { createStore, Store } from "@open-smc/store/src/store";
import { keyBy, keys, map, mapValues, pick } from "lodash";
import { Category, Named, SelectionByCategory } from "../../contract/application.contract";
import { MultiselectInner } from "./MultiselectInner";
import { useCategoryApi } from "../../useCategoryApi";
import { makeUseSelector } from "@open-smc/store/src/useSelector";

export interface MultiselectState {
    readonly selectedElements: Record<string, string[]>;
    readonly activeCategory?: string;
    readonly categories: Record<string, Category>;
    readonly elementsByCategory: Record<string, Record<string, Named>>;
    readonly categoriesToRefresh: string[];
}

const context = createContext<Store<MultiselectState>>(null);

export function useMultiselectStore() {
    return useContext(context);
}

export const useMultiselectSelector = makeUseSelector(useMultiselectStore);

interface Props {
    data: SelectionByCategory;
    categories: Category[];
    onChange: (data: SelectionByCategory) => void;
}

export const getSelectedElements = (model: SelectionByCategory) => mapValues(model, els => map(els, 'systemName'))

export function Multiselect({data, categories, onChange}: Props) {
    const [store, setStore] = useState<Store<MultiselectState>>();

    useEffect(() => {
        setStore(store => {
            const selectedElements = data ? getSelectedElements(data) : {};
            const categoryDict = keyBy(categories, 'category');
            const elementsByCategory = data ? mapValues(data, els => keyBy(els, 'systemName')) : {};
            const reloadCategories = keys(categoryDict);

            const activeCategory = store?.getState().activeCategory;

            return createStore<MultiselectState>({
                selectedElements,
                categories: categoryDict,
                activeCategory: activeCategory && categoryDict[activeCategory] ? activeCategory : categories[0]?.category,
                elementsByCategory,
                categoriesToRefresh: reloadCategories
            });
        })
    }, [data, categories]);

    useEffect(() =>
        store?.subscribe("selectedElements", () => {
            const {selectedElements, elementsByCategory} = store.getState();
            const data = keys(selectedElements).reduce<SelectionByCategory>((selection, category) => {
                selection[category] = map(selectedElements[category], el => elementsByCategory[category][el]);
                return selection;
            }, {});
            onChange?.(data);
        }), [store, onChange]);

    if (!store) {
        return null;
    }

    return (
        <context.Provider value={store}>
            <MultiselectInner/>
        </context.Provider>
    );
}

export function useSelectElement() {
    const {setState} = useMultiselectStore();
    const {sendCategoryChange} = useCategoryApi();

    return (systemName: string) => {
        const {activeCategory, selectedElements} =
            setState(({activeCategory, selectedElements}) => {
                if (!selectedElements[activeCategory]) {
                    selectedElements[activeCategory] = [];
                }
                selectedElements[activeCategory].push(systemName);
            });

        sendCategoryChange(pick(selectedElements, activeCategory));
    }
}

export function useUnselectElement() {
    const {setState} = useMultiselectStore();
    const {sendCategoryChange} = useCategoryApi();

    return (systemName: string) => {
        const {activeCategory, selectedElements} =
            setState(({activeCategory, selectedElements}) => {
                const index = selectedElements[activeCategory].indexOf(systemName);
                selectedElements[activeCategory].splice(index, 1);
            });

        sendCategoryChange(pick(selectedElements, activeCategory));
    }
}

export function useUnselectAll() {
    const {setState} = useMultiselectStore();
    const {sendCategoryChange} = useCategoryApi();

    return () => {
        const {categories} = setState(state => {
            state.selectedElements = {};
        });
        sendCategoryChange(keys(categories).reduce((prev, cur) => ({...prev, [cur]: null}), {}));
    }
}