import { useSetLoading } from "./useLoading";
import { useDeleteFile } from "./useDeleteFile";
import { useReloadFiles } from "./useReloadFiles";
import { FileModel } from "../fileExplorerState";
import { useSetFile } from "./useSetFile";
import { useNavigate } from "react-router-dom";
import { useProject } from "../../../projectStore/hooks/useProject";
import { useActiveFile } from "../../../projectStore/hooks/useActiveFile";
import { useEnv } from "../../../projectStore/hooks/useEnv";
import { PopupMenuItem } from "@open-smc/ui-kit/components/PopupMenu";

export function useCommonFileMenuItems(file: FileModel, canEdit: boolean) {
    const setFile = useSetFile();
    const setLoading = useSetLoading();
    const deleteFile = useDeleteFile();
    const reloadFiles = useReloadFiles();
    const navigate = useNavigate();
    const {project} = useProject();
    const {activeFile} = useActiveFile();
    const {env} = useEnv();

    return [
        {
            label: 'Settings',
            icon: 'sm sm-settings',
            qaAttribute: 'data-qa-btn-settings',
            onClick: () => navigate(`/project/${project.id}/env-settings/${env.id}/${file.id}`),
            visible: file.kind === 'Folder' || file.kind === 'Notebook'
        },
        {
            label: 'Access control',
            icon: 'sm sm-lock',
            qaAttribute: 'data-qa-btn-access',
            onClick: () => navigate(`/project/${project.id}/env-settings/${env.id}/${file.id}/access`)
        },
        {
            label: 'Rename',
            icon: 'sm sm-edit',
            qaAttribute: 'data-qa-btn-rename',
            onClick: () => {
                setFile(file.id, {editMode: true});
            },
            disabled: !canEdit
        },
        {
            label: 'Delete',
            icon: 'sm sm-trash',
            qaAttribute: 'data-qa-btn-delete',
            onClick: async () => {
                setLoading(true);
                await deleteFile(file.id);
                await reloadFiles();
                (activeFile?.path === file.path) && navigate(`/project/${project.id}/env/${env.id}`)
                setLoading(false);
            },
            disabled: !canEdit
        }
    ] as PopupMenuItem[];
}