export function insert(array: any[], index: number, ...elements: any[]) {
    return array.slice(0, index).concat(elements, array.slice(index));
}

export function humanFileSize(bytes: number, si = true, dp = 1) {
    const thresh = si ? 1000 : 1024;

    if (Math.abs(bytes) < thresh) {
        return bytes + ' B';
    }

    const units = si
        ? ['kB', 'MB', 'GB', 'TB', 'PB', 'EB', 'ZB', 'YB']
        : ['KiB', 'MiB', 'GiB', 'TiB', 'PiB', 'EiB', 'ZiB', 'YiB'];
    let u = -1;
    const r = 10 ** dp;

    do {
        bytes /= thresh;
        ++u;
    } while (Math.round(Math.abs(bytes) * r) / r >= thresh && u < units.length - 1);


    return bytes.toFixed(dp) + ' ' + units[u];
}

export const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));

export function isElementInViewport(el: Element) {
    const rect = el.getBoundingClientRect();
    const headersArray = document.getElementsByClassName('header-indent');
    let topIndentHeight = 0;
    if(headersArray?.length) {
        Array.prototype.forEach.call(
            headersArray,
            (headerElement) => topIndentHeight += headerElement.clientHeight
          );
    }

    return (
        rect.top >= topIndentHeight &&
        rect.left >= 0 &&
        rect.bottom <= (window.innerHeight || document.documentElement.clientHeight) && /* or $(window).height() */
        rect.right <= (window.innerWidth || document.documentElement.clientWidth) /* or $(window).width() */
    );
}