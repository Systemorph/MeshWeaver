import { useMultiselectSelector, useMultiselectStore, useSelectElement, useUnselectElement } from "./Multiselect";
import { startTransition, useEffect, useRef, useState } from "react";
import { isEmpty, keyBy, keys, trim, without } from "lodash";
import style from "./multiselect.module.scss";
import { Button } from "@open-smc/ui-kit/src/components/Button";
import classNames from "classnames";
import searchInput from "@open-smc/ui-kit/src/components/search.module.scss";
import loader from "@open-smc/ui-kit/src/components/loader.module.scss";
import { useCategoryApi } from "../../useCategoryApi";
import { InputText } from "@open-smc/ui-kit/src/components/InputText";
import Checkbox from "rc-checkbox";
import Switch from "rc-switch";
import "@open-smc/ui-kit/src/components/rc-switch.scss";

export function CategoryElements() {
    const {setState} = useMultiselectStore();
    const selectedElements = useMultiselectSelector('selectedElements');
    const activeCategory = useMultiselectSelector('activeCategory');
    const elementsByCategory = useMultiselectSelector('elementsByCategory');
    const categoriesToRefresh = useMultiselectSelector('categoriesToRefresh');
    const selectElement = useSelectElement();
    const unselectElement = useUnselectElement();
    const [search, setSearch] = useState('');
    const [selectedOnly, setSelectedOnly] = useState(false);
    const inputRef = useRef<HTMLInputElement>();
    const {sendCategoryRequest} = useCategoryApi();

    const setFocus = () => inputRef.current?.focus();

    const values = elementsByCategory[activeCategory];

    const refresh = categoriesToRefresh.includes(activeCategory);
    const loadActiveCategoryElements = !values || refresh;

    useEffect(() => {
        (async function () {
            if (loadActiveCategoryElements) {
                const elements = await sendCategoryRequest(activeCategory);

                setState(state => {
                    state.elementsByCategory = {
                        ...state.elementsByCategory,
                        [activeCategory]: keyBy(elements, 'systemName')
                    }
                });

                if (refresh) {
                    setState(state => {
                        state.categoriesToRefresh = without(state.categoriesToRefresh, activeCategory);
                    });
                }
            }
        })();
    }, [sendCategoryRequest, activeCategory, loadActiveCategoryElements, refresh]);

    if (!values && loadActiveCategoryElements) {
        return <div className={loader.loading}>Loading...</div>;
    }

    const cleanSearch = trim(search);

    const isSelected = (systemName: string) => selectedElements[activeCategory]?.includes(systemName);
    const matchesSearch = (displayName: string) => new RegExp(cleanSearch, "ig").test(displayName);

    const renderedElements = keys(values)
        .map(systemName => values[systemName])
        .filter(({displayName}) => isEmpty(cleanSearch) || matchesSearch(displayName))
        .filter(({systemName}) => !selectedOnly || isSelected(systemName))
        .map(({systemName, displayName}) => {
            const className = classNames(style.categoryElement);
            return (
                <li key={systemName}>
                    <label htmlFor={systemName} className={className}>
                        <Checkbox
                            id={systemName}
                            checked={isSelected(systemName)}
                            onChange={event =>
                                (event.target as HTMLInputElement).checked
                                    ? selectElement(systemName) : unselectElement(systemName)}
                        />
                        <span>{displayName}</span>
                    </label>
                </li>
            );
        });

    const selectedOnlyDisabled = !selectedElements[activeCategory]?.length;

    return (
        <div className={style.categoryValues}>
            <div className={searchInput.container}>
                <InputText ref={inputRef as any}
                           className={searchInput.input}
                           placeholder={'Search...'}
                           value={search}
                           onChange={e =>
                               startTransition(() => setSearch(e.target.value))}
                />
                <Button className={searchInput.button} icon="sm sm-search" onClick={setFocus}/>
                {cleanSearch && <Button className={searchInput.clearButton} icon="sm sm-close" onClick={() => {
                    setSearch('');
                    setFocus();
                }}/>}
            </div>
            <div className={style.switch}>
                <Switch
                    checked={selectedOnly}
                    onChange={checked => startTransition(() => setSelectedOnly(checked))}
                    disabled={selectedOnlyDisabled}
                />
                <span>Selected only</span>
            </div>
            <ul className={classNames(style.list, style.valuesList)}>
                {renderedElements}
            </ul>
        </div>
    );
}
