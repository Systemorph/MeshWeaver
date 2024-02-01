import {useProjectSelector} from "../projectStore";

// TODO: legacy (11/28/2023, akravets)
export function useActiveFile() {
    return useProjectSelector("activeFile");
}