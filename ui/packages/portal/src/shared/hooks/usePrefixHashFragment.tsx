import { useEffect } from "react";

//SOURCE https://github.com/rehypejs/rehype-sanitize#example-headings-dom-clobbering

const scrolltToElement = (target: HTMLElement) =>{
    // HACK imitation of 'target' pseudo class (14.10.2022, aberezutsky)
    // there is no possibility to use :target pseudo class as soon as url fragment is not equal to
    // id or name. That's why we have to add scroll margin manually and remove it after scroll.
    // It's not possible to assign it to generated a.anchor because user can produce it's own uncontrolled
    // html like <a name="some-id">test</a>
    target.classList.add('scroll-offset');
    target.scrollIntoView();
    target.classList.remove('scroll-offset');
}


const hashchange = (postpone = false) => {
    let hash;

    try {
        hash = decodeURIComponent(window.location.hash.slice(1)).toLowerCase();
    } catch {
        return;
    }

    const name = 'user-content-' + hash;
    const target =
        document.getElementById(name) || document.getElementsByName(name)[0];

    if(target){

        if(postpone){
            // HACK This timeout is needed because of list of async elements.
            // Elements will appear in future renderers which we can not predict.
            // Without timeout first will be scroll with proper placement of element.
            // But afterwards async elements will shift it to unpredicted position.
            // (21.10.2022, aberezutsky)
            setTimeout(()=>{
                scrolltToElement(target);
            }, 500);
        } else {
            scrolltToElement(target);
        }
    }
};

// When on the URL already, perhaps after scrolling, and clicking again, which
// doesn’t emit `hashchange`.
const clickOverwrite = (event: MouseEvent) => {
    if (
        event.target &&
        event.target instanceof HTMLAnchorElement &&
        event.target.href === window.location.href &&
        window.location.hash.length > 1
    ) {
        !event.defaultPrevented && hashchange();
    }
};

export const usePrefixHashFragment = () => {
    useEffect(() => {
        const hashchangeListener = () => hashchange(false);
        window.addEventListener('hashchange', hashchangeListener);
        hashchange(true);
        document.addEventListener('click', clickOverwrite, false);

        return () => {
            window.removeEventListener('hashchange', hashchangeListener)
            document.removeEventListener('click', clickOverwrite);
        }
    }, []);
};