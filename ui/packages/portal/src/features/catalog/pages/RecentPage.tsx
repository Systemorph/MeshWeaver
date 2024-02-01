import layoutStyles from '../layout.module.scss';
import toolBarStyles from "../components/toolbar.module.scss";
import loader from "@open-smc/ui-kit/components/loader.module.scss";
import React, { useEffect, useState } from "react";
import { ProjectApi, ProjectTuple, RecentTypeParam } from "../../../app/projectApi";
import classNames from "classnames";
import { Pills } from "../components/pills/Pills";
import {
    RECENT_TYPE_PARAM,
    SEARCH_PARAM,
    useSearchParamsUpdate
} from "../../../shared/hooks/useSearchParamsUpdate";
import { EmptyProjectCollection } from "../components/cards/EmptyProjectCollection";
import { ProjectCards } from "../components/cards/ProjectCards";
import { Search } from "../components/search/Search";
import { PAGE_PARAM, usePaginationParams } from "../../../shared/hooks/usePaginationParams";
import { Pagination } from "../../../shared/components/paginator/Pagination";

const PAGE_SIZE = 24;

export function RecentPage() {
    const [projectsData, setProjectsData] = useState<ProjectTuple>();

    const [searchParams, setSearchParams] = useSearchParamsUpdate();
    const recentType = searchParams.get(RECENT_TYPE_PARAM) as RecentTypeParam;
    const searchTerm = searchParams.get(SEARCH_PARAM);
    const [page, setPage] = usePaginationParams();

    useEffect(() => {
        (async () => {
            const projects = await ProjectApi.getRecentProjects(
                {
                    pageSize: PAGE_SIZE,
                    search: searchTerm,
                    page: page - 1,
                    mode: recentType
                });

            setProjectsData(projects);
        })()
    }, [searchTerm, recentType, page]);

    if (!projectsData) {
        return <div className={loader.loading}>Loading...</div>;
    }

    return (
        <div data-qa-catalog>
            <div className={classNames(toolBarStyles.toolbar, layoutStyles.toolbar)}>
                <h2 className={toolBarStyles.title}>Recent</h2>
                <Pills
                    pills={[
                        {key: 'all', label: 'All'},
                        {key: null, label: 'Mine'},
                        {key: 'public', label: 'Public'},
                    ]}
                    currentValue={recentType}
                    onChange={
                        ({key}) => {
                            setSearchParams(
                                [
                                    {key: RECENT_TYPE_PARAM, value: key},
                                    {key: PAGE_PARAM},
                                ]);
                        }}
                />
                <Search onSearch={(searchTerm) => {
                    setSearchParams(
                        [
                            {key: SEARCH_PARAM, value: searchTerm},
                            {key: PAGE_PARAM},
                        ]);
                }}/>
            </div>

            {projectsData.total !== 0 ? <ProjectCards projects={projectsData.projects}/> : <EmptyProjectCollection/>}

            <Pagination
                totalCount={projectsData.total}
                page={page}
                pageSize={PAGE_SIZE}
                onPageChanged={setPage}/>
        </div>
    )
}