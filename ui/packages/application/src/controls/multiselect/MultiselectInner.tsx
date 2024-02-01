import { keys } from "lodash";
import style from "./multiselect.module.scss";
import { CategoryElements } from "./CategoryElements";
import { useMultiselectSelector, useMultiselectStore, useUnselectAll } from "./Multiselect";
import classNames from "classnames";
import { Button } from "@open-smc/ui-kit/components/Button";
import buttons from "@open-smc/ui-kit/components/buttons.module.scss";

export function MultiselectInner() {
    const {setState} = useMultiselectStore();
    const selectedElements = useMultiselectSelector('selectedElements');
    const categories = useMultiselectSelector('categories');
    const activeCategory = useMultiselectSelector('activeCategory');
    const unselectAll = useUnselectAll();

    const isEmpty = keys(selectedElements).length === 0;

    const renderedCategories = keys(categories).map(category => {
        const {displayName} = categories[category];

        const className = classNames(style.categoryItem, {
            selected: selectedElements[category]?.length > 0,
            active: category === activeCategory,
        });

        const count = selectedElements[category]?.length;

        return (
            <li
                title={`${displayName}`}
                className={className}
                onClick={() => setState(state => {
                    state.activeCategory = category;
                })}
                key={category}
            >
                <span className={style.name}>{displayName}</span>
                {count && <span className={style.counter}>{count}</span>}
            </li>
        );
    })

    return (
        <div className={style.multiselectContainer}>
            <div className={style.listContainer}>
                <ul className={classNames(style.list, style.categoryList)}>
                    {renderedCategories}
                </ul>
                <CategoryElements/>
            </div>
            <Button label={'Reset all'}
                    icon="sm sm-undo"
                    className={classNames(buttons.button, style.resetAll)}
                    onClick={unselectAll}
                    disabled={isEmpty}
            />
        </div>
    );
}