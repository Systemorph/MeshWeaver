import styles from './cards.module.scss';
import { Link, useNavigate } from "react-router-dom";
import { Button } from "@open-smc/ui-kit/components/Button";
import classNames from "classnames";
import { format } from 'date-fns';
import { defaultThumbnail, ProjectCatalogItem } from "../../../../app/projectApi";
import { useSideMenu } from "../../../components/sideMenu/hooks/useSideMenu";
import { CloneProjectForm } from "../../../project/editProject/CloneProjectForm";
import button from "@open-smc/ui-kit/components/buttons.module.scss"
import PopupMenu from "@open-smc/ui-kit/components/PopupMenu";
import Dropdown from "rc-dropdown";


export function ProjectCard({name, id, createdOn, thumbnail, isPublic}: ProjectCatalogItem) {
    const {showMenu, closeMenu, hideMenu} = useSideMenu();
    const navigate = useNavigate();
    const popupMenu = (
        <PopupMenu
            className={classNames(
                "cls-qa-project-context-menu"
            )}
            menuItems={[
                {
                    label: "Clone",
                    icon: "sm sm-copy",
                    qaAttribute: "data-qa-btn-clone",
                    onClick: () => {
                        showMenu(
                            <CloneProjectForm
                                projectId={id}
                                onClose={closeMenu}
                                onCancel={closeMenu}
                                onFinish={(id) => {
                                    navigate(`/project/${id}`);
                                    closeMenu();
                                }}
                            />
                        );
                    },
                },
            ]}
        ></PopupMenu>
    );
    return (
        <div
            className={classNames(styles.card, {"public-project": isPublic})}
            data-qa-project={id}
        >
            <div className={styles.header}>
                <div className={styles.wrapper}>
                    <div className={styles.info}>
                        <span className={styles.date} data-qa-date>
                            {createdOn
                                ? format(new Date(createdOn), "LLL dd, yyyy")
                                : `Invalid date`}
                        </span>
                    </div>
                </div>
                <Dropdown
                    trigger={["click"]}
                    overlay={popupMenu}
                    align={{offset: [10, -10]}}
                >
                    <Button
                        className={classNames(styles.more, button.button)}
                        icon="sm sm-actions-more"
                        data-qa-btn-context-menu
                    />
                </Dropdown>
            </div>
            <Link
                to={`/project/${id}`}
                className={styles.cardLink}
                data-qa-link
            >
                <div className={styles.imagebox}>
                    <img
                        className={styles.image}
                        src={thumbnail ?? defaultThumbnail}
                        alt="card image"
                    />
                </div>
                <div className={styles.footer}>
                    <span className={styles.title} data-qa-name>
                        {name}
                    </span>
                </div>
            </Link>
        </div>
    );
}