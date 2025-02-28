export function isDarkMode() {
    let matched = window.matchMedia("(prefers-color-scheme: dark)").matches ;

    if (matched)
        return true;
    else
        return false;
    }