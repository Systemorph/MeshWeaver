import {ProjectCard} from "./ProjectCard";
import styles from './cards.module.scss';
import { ProjectCatalogItem } from "../../../../app/projectApi";

interface Props {
    projects: ProjectCatalogItem[];
    small?: boolean;
}

export function ProjectCards({projects, small}: Props) {
    return (
        <div className={styles.cardsbox}>
            <div className={`${styles.cards} ${small ? styles.small : ''}`}>
                {projects.map(project => <ProjectCard key={project.id} {...project} />)}
            </div>
        </div>
    );
}