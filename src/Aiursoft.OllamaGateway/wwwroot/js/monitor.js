import mermaid from '/node_modules/mermaid/dist/mermaid.esm.mjs';

mermaid.initialize({ layout: 'dagre', startOnLoad: false, theme: 'default' });
await mermaid.run({ nodes: [document.getElementById('traffic-topology')] });

for (const detail of document.querySelectorAll('#backend-route-details [data-backend-id]')) {
    const label = document.querySelector(`#traffic-topology .backend-route-${detail.dataset.backendId}`);
    if (!label) continue;
    label.setAttribute('tabindex', '0');
    label.setAttribute('title', detail.textContent);
    if (window.bootstrap?.Tooltip) {
        new bootstrap.Tooltip(label, {
            container: 'body',
            customClass: 'topology-tooltip',
            trigger: 'hover focus click',
            html: false
        });
    }
}
