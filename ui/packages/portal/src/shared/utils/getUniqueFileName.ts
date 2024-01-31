import path from "path-browserify";

export function getUniqueFileName(baseName: string, fileNames: string[]) {
    const {name, ext} = path.parse(baseName);
    const regExp = new RegExp(`^${name}(?:\\s\\((\\d+)\\))?$`, 'i');

    const indexArray: boolean[] = [];

    fileNames.forEach((filename) => {
        const {name: currentName, ext: currentExt} = path.parse(filename);
        if (ext.toLowerCase() === currentExt.toLowerCase()) {
            const match = currentName.match(regExp);
            if (match) {
                if (match[1] !== undefined) {
                    const index = parseInt(match[1]);
                    // only care about indexes that can conflict with auto-generated
                    // i.e. 2, 3, ...n, and ignoring 0, 00, 1, 01, 02 etc
                    if (index > 1 && index.toString() === match[1]) {
                        indexArray[index] = true;
                    }
                } else {
                    indexArray[1] = true;
                }
            }
        }
    });

    if (!indexArray.length) {
        return baseName;
    }

    for (let i = 1; i < indexArray.length; i++) {
        if (indexArray[i] === undefined) {
            return i===1 ? baseName : name + ` (${i})${ext}`;
        }
    }

    return name + ` (${indexArray.length})${ext}`;
}


