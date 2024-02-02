import { KeyboardEvent, useState } from "react";
import { useSearchParams } from "react-router-dom";
import Input from 'rc-input';
import {Button} from "@open-smc/ui-kit/src/components/Button";
import styles from "./search.module.scss"
import { isEmpty } from "lodash";
import { SEARCH_PARAM } from "../../../../shared/hooks/useSearchParamsUpdate";

export function Search({onSearch}: { onSearch: (searchTerm: string) => void }) {
    const [searchParams] = useSearchParams();

    const [searchTerm, setSearchTerm] = useState(searchParams.get(SEARCH_PARAM) || '');

    const handleSearchInput = (event: any) => {
        setSearchTerm(event.target.value);
        if (isEmpty(event.target.value)) {
            onSearch('')
        }
    };


    const onEnter = (event: KeyboardEvent<HTMLInputElement>) => {
        if (event.key === 'Enter' && !!searchTerm) {
            onSearch(searchTerm)
        }
    }

    return (
        <div className={styles.search}>
            <Input className={styles.searchInput} 
                   value={searchTerm} 
                   placeholder="Search..."
                   onInput={handleSearchInput} 
                   onKeyDown={onEnter} 
                   data-qa-search
                />
            <Button className={styles.searchButton} icon="sm sm-search" onClick={() => onSearch(searchTerm)} data-qa-search-btn />
        </div>
    );
}