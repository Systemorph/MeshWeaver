// Auto-scroll for the thread chat message list.
//
// Root-cause fix for #128: the old C# path scrolled via a Task.Yield() timing guess + an
// eval() querySelector, and only fired on message-COUNT changes — so it ran before the browser
// had painted the new height (undershoot) and never followed streamed text as it arrived.
//
// This module observes the ACTUAL scroll container and reacts to EVERY DOM mutation (new nodes
// AND streamed character data), scheduling the scroll inside requestAnimationFrame — the browser's
// native post-paint callback, so the height is always current when we scroll. It is class-agnostic
// (no dependency on per-bubble class names) and "sticks to bottom" only while the user is already
// near the bottom, so scrolling up to read history is never yanked back down.

export function attach(container) {
    if (!container)
        return null;

    // px from the bottom within which we consider the user "following" the conversation.
    const STICK_THRESHOLD = 120;

    let raf = 0;
    let stick = true;

    const distanceFromBottom = () =>
        container.scrollHeight - container.scrollTop - container.clientHeight;

    const toBottom = () => { container.scrollTop = container.scrollHeight; };

    // The user's own scrolling decides whether we keep following. Scrolling up to read history
    // releases the stick; scrolling back near the bottom re-engages it.
    const onScroll = () => { stick = distanceFromBottom() <= STICK_THRESHOLD; };
    container.addEventListener('scroll', onScroll, { passive: true });

    const observer = new MutationObserver(() => {
        cancelAnimationFrame(raf);
        raf = requestAnimationFrame(() => {
            if (stick)
                toBottom();
        });
    });
    observer.observe(container, { childList: true, subtree: true, characterData: true });

    // Snap to the bottom once on attach so an opened thread starts at the latest message.
    requestAnimationFrame(toBottom);

    return {
        dispose: () => {
            cancelAnimationFrame(raf);
            observer.disconnect();
            container.removeEventListener('scroll', onScroll);
        }
    };
}
