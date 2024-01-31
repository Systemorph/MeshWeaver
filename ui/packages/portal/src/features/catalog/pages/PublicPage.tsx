import toolBarStyles from "../components/toolbar.module.scss";
import loader from "@open-smc/ui-kit/components/loader.module.scss";
import React, { useEffect, useState } from "react";
import { ProjectApi, ProjectTuple } from "../../../app/projectApi";
import { Search } from "../components/search/Search";
import { EmptyProjectCollection } from "../components/cards/EmptyProjectCollection";
import { ProjectCards } from "../components/cards/ProjectCards";
import { PAGE_PARAM, usePaginationParams } from "../../../shared/hooks/usePaginationParams";
import { SEARCH_PARAM, useSearchParamsUpdate } from "../../../shared/hooks/useSearchParamsUpdate";
import { Pagination } from "../../../shared/components/paginator/Pagination";

const PAGE_SIZE = 24;

export function PublicPage() {
    const [projectsData, setProjectsData] = useState<ProjectTuple>();
    const [searchParams, setSearchParams] = useSearchParamsUpdate();
    const searchTerm = searchParams.get(SEARCH_PARAM);
    const [page, setPage] = usePaginationParams();

    useEffect(() => {
        (async () => {
            const projects = await ProjectApi.getPublicProjects(
                {
                    pageSize: PAGE_SIZE,
                    search: searchTerm,
                    page: page - 1
                });

            setProjectsData(projects);
        })()
    }, [searchTerm, page]);

    if (!projectsData) {
        return <div className={loader.loading}>Loading...</div>;
    }

    return (
        <div data-qa-catalog>
            <div className={toolBarStyles.toolbar}>
                <h2 className={toolBarStyles.title}>Public</h2>

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