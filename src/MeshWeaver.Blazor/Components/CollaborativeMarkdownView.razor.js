const minCardGap = 12;

function getAnnotationIdFromCard(card) {
    const cls = Array.from(card.classList).find(c => c.startsWith('annotation-for-'));
    return cls ? cls.replace('annotation-for-', '') : null;
}

function getAnnotationColor(card) {
    if (card.classList.contains('annotation-type-insert')) return '#22c55e';
    if (card.classList.contains('annotation-type-delete')) return '#ef4444';
    if (card.classList.contains('annotation-type-comment')) return '#3b82f6';
    return '#6b7280';
}

function drawConnectingLines(container, annotationsCol) {
    if (!container || !annotationsCol) return;

    const oldSvg = annotationsCol.parentElement?.querySelector('.annotation-connecting-lines');
    if (oldSvg) oldSvg.remove();

    const splitLayout = annotationsCol.parentElement;
    if (!splitLayout) return;

    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.classList.add('annotation-connecting-lines');
    const svgHeight = Math.max(splitLayout.scrollHeight, container.scrollHeight, annotationsCol.scrollHeight);
    svg.style.cssText = 'position:absolute;top:0;left:0;pointer-events:none;z-index:1;overflow:visible;';
    svg.setAttribute('width', splitLayout.scrollWidth);
    svg.setAttribute('height', svgHeight);
    splitLayout.insertBefore(svg, splitLayout.firstChild);

    const splitRect = splitLayout.getBoundingClientRect();
    const cards = annotationsCol.querySelectorAll('.annotation-card.annotation-type-comment');

    cards.forEach(card => {
        const annotationId = getAnnotationIdFromCard(card);
        if (!annotationId) return;
        const color = getAnnotationColor(card);
        const marker = container.querySelector(`[data-comment-id="${annotationId}"]`);

        if (marker && card.offsetParent) {
            const markerRect = marker.getBoundingClientRect();
            const cardRect = card.getBoundingClientRect();

            const markerX = markerRect.right - splitRect.left + splitLayout.scrollLeft + 4;
            const markerY = markerRect.top + markerRect.height / 2 - splitRect.top + splitLayout.scrollTop;
            const cardX = cardRect.left - splitRect.left + splitLayout.scrollLeft - 4;
            const cardY = cardRect.top + Math.min(24, cardRect.height / 2) - splitRect.top + splitLayout.scrollTop;

            const midX = (markerX + cardX) / 2;
            const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
            path.setAttribute('d', `M ${markerX} ${markerY} Q ${midX} ${markerY} ${midX} ${(markerY + cardY) / 2} Q ${midX} ${cardY} ${cardX} ${cardY}`);
            path.setAttribute('stroke', color);
            path.setAttribute('stroke-width', '1.5');
            path.setAttribute('fill', 'none');
            path.setAttribute('opacity', '0.5');
            path.setAttribute('data-annotation', annotationId);
            svg.appendChild(path);

            const circle = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
            circle.setAttribute('cx', markerX);
            circle.setAttribute('cy', markerY);
            circle.setAttribute('r', '3');
            circle.setAttribute('fill', color);
            circle.setAttribute('opacity', '0.7');
            svg.appendChild(circle);
        }
    });
}

export function positionAnnotations(contentEl, sidebarEl) {
    if (!contentEl || !sidebarEl) return;

    const cards = sidebarEl.querySelectorAll('.annotation-card.annotation-type-comment');
    const positions = [];

    cards.forEach(card => {
        const annotationId = getAnnotationIdFromCard(card);
        if (!annotationId) return;

        const marker = contentEl.querySelector(`[data-comment-id="${annotationId}"]`);
        if (marker) {
            const markerRect = marker.getBoundingClientRect();
            const colRect = sidebarEl.getBoundingClientRect();
            const desiredTop = markerRect.top - colRect.top + sidebarEl.scrollTop;

            positions.push({
                card,
                annotationId,
                desiredTop,
                height: card.offsetHeight || 80
            });
        }
    });

    positions.sort((a, b) => a.desiredTop - b.desiredTop);

    let lastBottom = 0;
    positions.forEach(pos => {
        const actualTop = Math.max(pos.desiredTop, lastBottom + minCardGap);
        pos.actualTop = actualTop;
        lastBottom = actualTop + pos.height;
    });

    positions.forEach(pos => {
        pos.card.style.position = 'absolute';
        pos.card.style.top = pos.actualTop + 'px';
        pos.card.style.left = '0';
        pos.card.style.right = '0';
    });

    if (positions.length > 0) {
        const lastPos = positions[positions.length - 1];
        sidebarEl.style.minHeight = (lastPos.actualTop + lastPos.height + 16) + 'px';
    }

    requestAnimationFrame(() => drawConnectingLines(contentEl, sidebarEl));
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

    // Highlight SVG line
    const svg = document.querySelector('.annotation-connecting-lines');
    if (svg) {
        svg.querySelectorAll('path').forEach(p => {
            if (p.dataset.annotation === annotationId) {
                p.setAttribute('opacity', '0.8');
                p.setAttribute('stroke-width', '2');
            } else {
                p.setAttribute('opacity', '0.3');
                p.setAttribute('stroke-width', '1');
            }
        });
    }
}
