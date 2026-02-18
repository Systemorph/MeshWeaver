export function init(contentEl) {
    if (!contentEl) return;

    contentEl.addEventListener('click', function (event) {
        const annotationEl = event.target.closest('[data-comment-id], [data-change-id]');
        if (annotationEl) {
            event.stopPropagation();
            const id = annotationEl.dataset.commentId || annotationEl.dataset.changeId;
            if (id) {
                highlightAnnotation(id);
            }
        }
    });
}

export function highlightAnnotation(annotationId) {
    // Remove previous highlights
    document.querySelectorAll('.annotation-active').forEach(el => el.classList.remove('annotation-active'));
    document.querySelectorAll('.annotation-card.active').forEach(el => el.classList.remove('active'));

    // Highlight marker in content
    const container = document.querySelector('.markdown-annotations-container');
    if (container) {
        const marker = container.querySelector(`[data-comment-id="${annotationId}"]`) ||
                       container.querySelector(`[data-change-id="${annotationId}"]`);
        if (marker) {
            marker.classList.add('annotation-active');
            marker.scrollIntoView({ behavior: 'smooth', block: 'center' });
        }
    }

    // Highlight card in side panel
    const card = document.querySelector(`.annotation-for-${annotationId}`);
    if (card) {
        card.classList.add('active');
        card.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    }
}
