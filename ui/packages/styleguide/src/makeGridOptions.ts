import { chance } from "./chance";

export function makeGridOptions(rowNumber = 10) {
    const rowData = chance.n(
        () => (
            {
                company: chance.company(),
                owner: chance.name(),
                country: chance.country({full: true})
            }
        ),
        rowNumber
    );

    const columnDefs = [
        {field: 'company'},
        {field: 'owner'},
        {field: 'country'}
    ];

    return {
        rowData,
        columnDefs
    };
}