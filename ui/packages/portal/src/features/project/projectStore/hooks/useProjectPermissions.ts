import { ProjectState } from "../projectState";
import { useSelector } from "../projectStore";

export const permissionsSelector = ({permissions}: ProjectState) => permissions;

export function useProjectPermissions() {
    return useSelector(permissionsSelector);
}