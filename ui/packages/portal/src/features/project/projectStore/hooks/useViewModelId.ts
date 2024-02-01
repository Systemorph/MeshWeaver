import { ProjectState } from "../projectState";
import { useSelector } from "../projectStore";

const viewModelIdSelector = ({viewModelId}: ProjectState) => viewModelId;

export function useViewModelId() {
    return useSelector(viewModelIdSelector);
}