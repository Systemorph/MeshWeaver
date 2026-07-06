// Original-document viewer interop for DocumentSourceView.
//
// Renders a PDF (via PDF.js) or a DOCX (via mammoth) inline and highlights a passage in the rendered
// text. Both libraries are vendored under /lib (libman.json) so the viewer works on offline/prod
// images — never a CDN. Any failure (library missing, unsupported type, fetch error) degrades to a
// download link plus the passage text, so the pane is never silently blank.

// Resolve the vendored libraries relative to THIS module's URL so the paths are correct wherever the
// RCL static assets are mounted (they serve under _content/MeshWeaver.Blazor/…, not /lib).
const LIB_BASE = new URL('../lib/', import.meta.url);
const PDFJS_URL = new URL('pdfjs-dist/build/pdf.mjs', LIB_BASE).href;
const PDFJS_WORKER_URL = new URL('pdfjs-dist/build/pdf.worker.mjs', LIB_BASE).href;
const MAMMOTH_URL = new URL('mammoth/mammoth.browser.min.js', LIB_BASE).href;

let pdfjsPromise = null;
let mammothPromise = null;

/** Render `fileUrl` into `container` and highlight `highlight`. Entry point called from .NET.
 *  `page` (1-based) + `bbox` ({x,y,w,h} normalized, or its JSON) mark the exact region on a PDF page. */
export async function render(container, fileUrl, mime, highlight, fileName, page, bbox) {
    if (!container) return;
    container.innerHTML = '<div class="mw-docsource-loading">Loading document…</div>';

    const kind = classify(mime, fileUrl);
    const box = parseBox(bbox);
    try {
        if (kind === 'pdf') {
            await renderPdf(container, fileUrl, highlight, page, box);
        } else if (kind === 'docx') {
            await renderDocx(container, fileUrl, highlight);
        } else {
            fallback(container, fileUrl, highlight, fileName,
                'Inline preview is not available for this file type.');
        }
    } catch (err) {
        console.warn('DocumentSourceView render failed', err);
        fallback(container, fileUrl, highlight, fileName,
            'The document could not be displayed inline.');
    }
}

/** Normalize the bbox argument (a {x,y,w,h} object or its JSON string) to an object, or null. */
function parseBox(bbox) {
    if (!bbox) return null;
    if (typeof bbox === 'object') return bbox;
    try {
        const b = JSON.parse(bbox);
        return (b && typeof b === 'object') ? b : null;
    } catch {
        return null;
    }
}

/** Best-effort teardown — clears the container so a re-mount starts clean. */
export function dispose(container) {
    if (container) container.innerHTML = '';
}

function classify(mime, url) {
    const m = (mime || '').toLowerCase();
    const u = (url || '').toLowerCase();
    if (m.includes('pdf') || u.endsWith('.pdf')) return 'pdf';
    if (m.includes('wordprocessingml') || u.endsWith('.docx')) return 'docx';
    return 'other';
}

// ── PDF (PDF.js) ────────────────────────────────────────────────────────────

function loadPdfjs() {
    if (!pdfjsPromise) {
        pdfjsPromise = import(PDFJS_URL).then((lib) => {
            lib.GlobalWorkerOptions.workerSrc = PDFJS_WORKER_URL;
            return lib;
        });
    }
    return pdfjsPromise;
}

async function renderPdf(container, url, highlight, targetPage, box) {
    const pdfjsLib = await loadPdfjs();
    const doc = await pdfjsLib.getDocument({ url }).promise;

    container.innerHTML = '';
    const pages = document.createElement('div');
    pages.className = 'mw-docsource-pages';
    container.appendChild(pages);

    // If a stored bbox is given, remember the target page's element + viewport so we can overlay the
    // highlight rectangle once it's rendered (precise, unlike the string-match fallback).
    let markPageEl = null;
    let markViewport = null;

    for (let n = 1; n <= doc.numPages; n++) {
        const page = await doc.getPage(n);
        const viewport = page.getViewport({ scale: 1.35 });

        const pageEl = document.createElement('div');
        pageEl.className = 'mw-pdf-page';
        pageEl.style.position = 'relative';
        pageEl.style.width = viewport.width + 'px';
        pageEl.style.height = viewport.height + 'px';

        const canvas = document.createElement('canvas');
        canvas.width = Math.floor(viewport.width);
        canvas.height = Math.floor(viewport.height);
        pageEl.appendChild(canvas);

        await page.render({ canvasContext: canvas.getContext('2d'), viewport }).promise;

        // A transparent text layer over the canvas makes the text selectable AND lets the <mark>
        // highlight show through as a coloured rectangle over the rendered glyphs.
        const textLayer = document.createElement('div');
        textLayer.className = 'mw-pdf-textlayer';
        textLayer.style.width = viewport.width + 'px';
        textLayer.style.height = viewport.height + 'px';
        pageEl.appendChild(textLayer);
        try {
            const textContent = await page.getTextContent();
            if (pdfjsLib.TextLayer) {
                await new pdfjsLib.TextLayer({ textContentSource: textContent, container: textLayer, viewport }).render();
            } else if (pdfjsLib.renderTextLayer) {
                await pdfjsLib.renderTextLayer({ textContentSource: textContent, container: textLayer, viewport }).promise;
            }
        } catch (e) {
            // Text layer is best-effort — without it the page still renders, just without highlight.
            console.warn('PDF text layer failed on page ' + n, e);
        }

        pages.appendChild(pageEl);
        if (box && targetPage === n) {
            markPageEl = pageEl;
            markViewport = viewport;
        }
    }

    // Prefer the precise stored-bbox rectangle; fall back to the verbatim text match when there's none.
    let boxMark = null;
    if (markPageEl && box)
        boxMark = drawBoxOverlay(markPageEl, markViewport, box);
    const textMark = highlightInElement(container, highlight);
    scrollToMark(boxMark || textMark);
}

/**
 * Overlays a highlight rectangle at the normalized `box` ({x,y,w,h}, top-left origin) on a rendered page.
 * The page element is positioned relative, so the absolute rectangle lands over the exact glyphs.
 */
function drawBoxOverlay(pageEl, viewport, box) {
    const el = document.createElement('div');
    el.className = 'mw-pdf-boxmark';
    el.style.position = 'absolute';
    el.style.left = (box.x * viewport.width) + 'px';
    el.style.top = (box.y * viewport.height) + 'px';
    el.style.width = Math.max(4, box.w * viewport.width) + 'px';
    el.style.height = Math.max(4, box.h * viewport.height) + 'px';
    el.style.background = 'rgba(255, 209, 0, 0.30)';
    el.style.outline = '2px solid #ffb300';
    el.style.borderRadius = '2px';
    el.style.pointerEvents = 'none';
    el.style.zIndex = '3';
    pageEl.appendChild(el);
    return el;
}

// ── DOCX (mammoth) ───────────────────────────────────────────────────────────

function loadMammoth() {
    if (!mammothPromise) {
        mammothPromise = loadScript(MAMMOTH_URL).then(() => window.mammoth);
    }
    return mammothPromise;
}

async function renderDocx(container, url, highlight) {
    const mammoth = await loadMammoth();
    const resp = await fetch(url);
    if (!resp.ok) throw new Error('fetch failed: ' + resp.status);
    const arrayBuffer = await resp.arrayBuffer();
    const result = await mammoth.convertToHtml({ arrayBuffer });

    container.innerHTML = '';
    const doc = document.createElement('div');
    doc.className = 'mw-docx-body markdown-body';
    doc.innerHTML = result.value || '';
    container.appendChild(doc);

    const first = highlightInElement(container, highlight);
    scrollToMark(first);
}

// ── Highlight (shared) ───────────────────────────────────────────────────────

/** Free-text terms worth marking: drop grammar tokens (key:value), @paths, and single chars. */
function terms(query) {
    if (!query) return [];
    const out = [];
    const seen = new Set();
    for (const raw of query.split(/\s+/)) {
        if (!raw || raw.includes(':') || raw.startsWith('@')) continue;
        const t = raw.replace(/^["'(,.;]+|["')(,.;]+$/g, '');
        if (t.length < 2) continue;
        const key = t.toLowerCase();
        if (!seen.has(key)) { seen.add(key); out.push(t); }
    }
    return out;
}

/** Wraps every term match (case-insensitive) inside `root`'s text nodes in <mark>. Returns the first mark. */
function highlightInElement(root, query) {
    const list = terms(query);
    if (!root || list.length === 0) return null;

    const pattern = new RegExp('(' + list.map(escapeRegExp).join('|') + ')', 'gi');
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
        acceptNode: (node) =>
            node.nodeValue && node.nodeValue.trim() && node.parentNode && node.parentNode.nodeName !== 'MARK'
                ? NodeFilter.FILTER_ACCEPT
                : NodeFilter.FILTER_REJECT,
    });

    const targets = [];
    let n;
    while ((n = walker.nextNode())) {
        pattern.lastIndex = 0;
        if (pattern.test(n.nodeValue)) targets.push(n);
    }

    let firstMark = null;
    for (const node of targets) {
        const frag = document.createDocumentFragment();
        let last = 0;
        const text = node.nodeValue;
        pattern.lastIndex = 0;
        let m;
        while ((m = pattern.exec(text)) !== null) {
            if (m.index > last) frag.appendChild(document.createTextNode(text.slice(last, m.index)));
            const mark = document.createElement('mark');
            mark.className = 'mw-docmark';
            mark.textContent = m[0];
            frag.appendChild(mark);
            firstMark = firstMark || mark;
            last = m.index + m[0].length;
            if (m[0].length === 0) pattern.lastIndex++; // guard against zero-width loops
        }
        if (last < text.length) frag.appendChild(document.createTextNode(text.slice(last)));
        node.parentNode.replaceChild(frag, node);
    }
    return firstMark;
}

function scrollToMark(mark) {
    if (mark && mark.scrollIntoView) {
        try { mark.scrollIntoView({ block: 'center', behavior: 'smooth' }); } catch { /* older browsers */ }
    }
}

// ── Fallback + helpers ───────────────────────────────────────────────────────

function fallback(container, fileUrl, highlight, fileName, reason) {
    container.innerHTML = '';
    const box = document.createElement('div');
    box.className = 'mw-docsource-fallback';

    const note = document.createElement('p');
    note.className = 'mw-docsource-note';
    note.textContent = reason;
    box.appendChild(note);

    const link = document.createElement('a');
    link.href = fileUrl + (fileUrl.includes('?') ? '&' : '?') + 'download';
    link.className = 'mw-docsource-download';
    link.textContent = 'Download ' + (fileName || 'the original file');
    box.appendChild(link);

    if (highlight && terms(highlight).length) {
        const passage = document.createElement('div');
        passage.className = 'mw-docsource-passage';
        passage.textContent = highlight;
        box.appendChild(passage);
        highlightInElement(passage, highlight);
    }
    container.appendChild(box);
}

function loadScript(src) {
    return new Promise((resolve, reject) => {
        const existing = document.querySelector('script[data-mw-src="' + src + '"]');
        if (existing) {
            if (existing.dataset.loaded === 'true') resolve();
            else {
                existing.addEventListener('load', () => resolve());
                existing.addEventListener('error', () => reject(new Error('script load failed: ' + src)));
            }
            return;
        }
        const el = document.createElement('script');
        el.src = src;
        el.async = true;
        el.dataset.mwSrc = src;
        el.addEventListener('load', () => { el.dataset.loaded = 'true'; resolve(); });
        el.addEventListener('error', () => reject(new Error('script load failed: ' + src)));
        document.head.appendChild(el);
    });
}

function escapeRegExp(s) {
    return s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}
