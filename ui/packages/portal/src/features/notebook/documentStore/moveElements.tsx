export function moveElements(elementIds: string[], elementIdsToMove: string[], afterElementId: string) {
    let start;

    for (let i = 0; i < elementIds.length; i++) {
        if (start === undefined && elementIds[i] === elementIdsToMove[0]) {
            for (let j = 0; j < elementIdsToMove.length; j++) {
                if (elementIds[i + j] !== elementIdsToMove[j]) {
                    throw 'Wrong sequence of elements';
                }
            }

            start = i;
            break;
        }
    }

    if (start === undefined) {
        throw 'Wrong sequence of elements';
    }

    const result = [...elementIds];

    result.splice(start, elementIdsToMove.length);

    let newStart;

    if (afterElementId !== null && afterElementId !== undefined) {
        const index = result.indexOf(afterElementId);

        if (index === -1) {
            throw 'Wrong afterElementId argument';
        }

        newStart = index + 1;
    }
    else {
        newStart = 0;
    }

    result.splice(newStart, 0, ...elementIdsToMove);

    return result;
}