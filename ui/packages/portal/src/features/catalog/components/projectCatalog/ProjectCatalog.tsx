import {useState} from "react";
import {TabView, TabPanel} from "primereact/tabview";
import styles from "./notebooks-catalog.module.scss";
import {ProjectCards} from "../cards/ProjectCards";
import {AgGridReact} from "ag-grid-react";
import { ColDef } from "ag-grid-community";
import { Link } from "react-router-dom";
import "./tab-view.scss"
import {Button} from "@open-smc/ui-kit/components/Button";
import "../../../../ag-grid-theme.scss"
import { Project } from "../../../../app/projectApi";
import {format} from 'date-fns';
import { map } from "lodash";

type Props = {
    projects: Project[];
};

const columnDefs: ColDef[] = [

    {
        headerName: "Name",
        field: "name",
        flex: 1,
        cellRendererFramework: ({data}: {data: Project}) => <Link to={`/project/${data.id}`}>{data.name}</Link>
    },
    {
        headerName: "Created On",
        field: "createdOn",
        flex: 1,
        valueFormatter: ({data}: {data: Project}) => data.createdOn ? format(new Date(data.createdOn), "LLL dd, yyyy") : `Invalid date`,
    },
    {
        headerName: "Author",
        flex: 1,
        valueGetter: ({data}: {data: Project}) => map(data.authors, 'name').join(', ')
    },
    {
        width: 32,
        cellClass: 'cellMore',
        cellRendererFramework: () => <Button className={styles.more} icon="sm sm-actions-more"/>
    },
];

export function ProjectCatalog({projects}: Props) {
    const [isGridView, setGridView] = useState(false);
    const headerHeight = 16;

    return (
        <div className={styles.container}>
            <TabView className={styles.tab}>
                <TabPanel header="Recent" className={styles.panel}>
                    {isGridView ? (
                        <ProjectCards projects={projects} small={true} />
                    ) : (
                        <div
                            className="ag-theme-alpine"
                        >
                            <AgGridReact
                                rowData={projects}
                                columnDefs={columnDefs}
                                headerHeight={headerHeight}
                                domLayout={'autoHeight'}
                            />
                        </div>
                    )}
                </TabPanel>
                <TabPanel className={styles.title} header="Pinned">
                    <div className={styles.content}>Content II</div>
                </TabPanel>
                <TabPanel header="Shared with me">
                    <div className={styles.content}>Content III</div>
                </TabPanel>
            </TabView>
            <div className={styles.icons}>
                <Button className={`${styles.icon} ${!isGridView? styles.active : ''}`} icon="sm sm-list-blocks" onClick={() => setGridView(false)}/>
                <Button className={`${styles.icon} ${isGridView? styles.active : ''}`} icon="sm sm-grid" onClick={() => setGridView(true)}/>
            </div>
        </div>
    );
}
