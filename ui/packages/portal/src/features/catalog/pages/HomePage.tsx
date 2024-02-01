import styles from '../layout.module.scss';
import style from "../components/toolbar.module.scss";
import loader from "@open-smc/ui-kit/components/loader.module.scss";
import button from "@open-smc/ui-kit/components/buttons.module.scss"
import React, { useEffect, useState } from "react";
import { ProjectCards } from "../components/cards/ProjectCards";
import { ProjectApi, ProjectCatalogItem, ProjectTuple, RecentTypeParam } from "../../../app/projectApi";
import { useNavigate } from "react-router-dom";
import classNames from "classnames";
import { Button } from "@open-smc/ui-kit/components/Button";
import { EmptyProjectCollection } from "../components/cards/EmptyProjectCollection";
import { Pills } from "../components/pills/Pills";
import {
    getSearchParams,
    RECENT_TYPE_PARAM,
    SEARCH_PARAM,
    useSearchParamsUpdate,
} from "../../../shared/hooks/useSearchParamsUpdate";
import { Search } from "../components/search/Search";

const PUBLIC_AMOUNT = 4;
const RECENT_AMOUNT = 8;

const ViewMoreButton = ({navigateTo}: {navigateTo: string}) => {
    const navigate = useNavigate();
    return (<Button
            data-qa-btn-view-more
            className={classNames(button.primaryButton, button.button, styles.viewMoreButton)}
            label="View more"
            onClick={() => navigate(navigateTo)}
        />
    )
}

export function HomePage() {
    const [publicProjects, setPublicProjects] = useState<ProjectTuple>();
    const [recentProjects, setRecentProjects] = useState<ProjectTuple>();
    const navigate = useNavigate();
    const [searchParams, setSearchParams] = useSearchParamsUpdate();
    const recentType = searchParams.get(RECENT_TYPE_PARAM) as RecentTypeParam;

    useEffect(() => {
        (async () => {
            const projects = await ProjectApi.getPublicProjects({pageSize: PUBLIC_AMOUNT});
            setPublicProjects(projects);
        })()
    }, []);

    useEffect(() => {
        (async () => {
            const projects = await ProjectApi.getRecentProjects({
                pageSize: RECENT_AMOUNT,
                mode: recentType
            });
            setRecentProjects(projects);
        })()
    }, [recentType]);

    if (!publicProjects || !recentProjects) {
        return <div className={loader.loading}>Loading...</div>;
    }

    return (
        <div data-qa-catalog>
            {publicProjects.total !== 0 && <>
                <div className={style.toolbar} data-qa-toolbar>
                    <span className={style.title} data-qa-toolbar-name='Public'>Public</span>
                    <Search onSearch={(searchTerm) => {
                        navigate(`/public?${
                            getSearchParams([{
                                key: SEARCH_PARAM,
                                value: searchTerm
                            }])}`)
                    }}/>
                </div>
                <ProjectCards projects={publicProjects.projects}/>
                {(publicProjects.total > PUBLIC_AMOUNT) && <ViewMoreButton navigateTo={'/public'}/>}
            </>}

            <div className={classNames(style.toolbar, styles.toolbar)} data-qa-toolbar>
                <span className={style.title} data-qa-toolbar-name='Recent'>Recent</span>

                <Pills pills={[
                    {key: 'all', label: 'All'},
                    {key: null, label: 'Mine'},
                    {key: 'public', label: 'Public'},
                ]}
                       currentValue={recentType}
                       onChange={
                           ({key}) => {
                               setSearchParams(
                                   [
                                       {key: RECENT_TYPE_PARAM, value: key}
                                   ]);
                           }}
                />

                <Search onSearch={(searchTerm) => {
                    navigate(`/recent?${
                        getSearchParams([
                            {
                                key: SEARCH_PARAM,
                                value: searchTerm
                            },
                            {
                                key: RECENT_TYPE_PARAM,
                                value: recentType
                            }
                        ])}`)
                }}/>
            </div>

            {(recentProjects.total !== 0) ? <ProjectCards projects={recentProjects.projects}/> :
                <EmptyProjectCollection/>}

            {(recentProjects.total > RECENT_AMOUNT) &&
                <ViewMoreButton navigateTo={`recent?${getSearchParams([
                    {
                        key: RECENT_TYPE_PARAM,
                        value: recentType
                    }
                ])}`}/>
            }
        </div>
    )
}