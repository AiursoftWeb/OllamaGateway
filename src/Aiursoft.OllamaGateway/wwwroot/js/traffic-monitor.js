// The server owns the timeline. A marker's movement between observed stages is illustrative.
export function startTrafficMonitor(svg) {
    const controls = document.getElementById('traffic-controls');
    const labels = Object.fromEntries([...document.querySelectorAll('#traffic-labels [data-key]')]
        .map(el => [el.dataset.key, el.textContent]));
    const status = document.getElementById('traffic-status');
    const picker = document.getElementById('traffic-request');
    const detail = document.getElementById('traffic-detail');
    const pause = document.getElementById('traffic-pause');
    const modelNames = new Map([...document.querySelectorAll('#backend-route-details [data-model]')]
        .map(el => [Number(el.dataset.model), el.textContent]));
    const routes = new Map([...document.querySelectorAll('#backend-route-details [data-route]')]
        .map(el => [Number(el.dataset.route), el]));
    const paths = new Map([...svg.querySelectorAll('path.flowchart-link[data-id]')].map(path => [
        path.dataset.id, { path, length: path.getTotalLength() }
    ]));
    const ns = 'http://www.w3.org/2000/svg';
    const layer = document.createElementNS(ns, 'g');
    layer.classList.add('traffic-markers');
    svg.append(layer);
    const groups = new Map();
    const highlighted = new Set();
    const nodes = new Map();
    for (const node of svg.querySelectorAll('g.node')) {
        const id = [...node.classList].find(name => name.startsWith('traffic-'));
        if (id) nodes.set(id.slice(8), node);
    }
    const clusters = new Map([...svg.querySelectorAll('g.cluster')].map(el => [el.id.slice(svg.id.length + 1), el]));
    const reduced = matchMedia('(prefers-reduced-motion: reduce)');
    const windowMs = 30000;
    // Brief handoffs stay legible even if several server events arrive in the same frame.
    const dwell = { received: 2400, selected: 1000, attemptFailed: 650 };
    const returnMs = 3600;
    const arrivalHold = 1000;
    const schedules = new Map();
    let baseline = new Map();
    let history = [];
    let floor = 0;
    let offset = 0;
    let frozen = null;
    let connected = false;
    let stoppedAt = Date.now();
    let dirty = true;
    let lastList = 0;
    let controlsChanged = true;
    let frame;
    const clock = () => connected ? Date.now() + offset : stoppedAt;

    function apply(state, event) {
        if (event.finished) state.delete(event.requestId);
        else state.set(event.requestId, event);
    }

    function trim(now) {
        let cut = Math.max(0, history.length - 4096);
        while (cut < history.length && history[cut].at < now - windowMs) cut++;
        for (let i = 0; i < cut; i++) apply(baseline, history[i]);
        if (cut) {
            floor = Math.max(floor, history[cut - 1].at);
            history = history.slice(cut);
        }
    }

    const feed = new EventSource(controls.dataset.feed);
    feed.onmessage = ({ data }) => {
        const batch = JSON.parse(data);
        if (batch.baseline) {
            for (const node of svg.querySelectorAll('.virtualSvcBusy, .physicalBusy'))
                node.classList.remove('virtualSvcBusy', 'physicalBusy');
            baseline = new Map(batch.baseline.map(e => [e.requestId, e]));
            history = [];
            schedules.clear();
            floor = batch.events[0]?.at ?? batch.serverTime;
            frozen = null;
        }
        for (const e of batch.events) {
            const previous = schedules.get(e.requestId) ?? baseline.get(e.requestId);
            e.previousStage = previous?.stage;
            // A text delta updates the label on the same moving marker.
            e.previewUpdate = previous?.stage === e.stage && previous?.attempt === e.attempt;
            if (e.previewUpdate) {
                e.drawAt = previous.drawAt ?? previous.at;
                e.stageAt = previous.stageAt ?? previous.at;
            } else {
                e.drawAt = Math.min(e.at + 4000, Math.max(e.at,
                    (previous?.drawAt ?? e.at) + (dwell[previous?.stage] ?? 0)));
                e.stageAt = e.at;
            }
            if (e.stage === 'completed') {
                const start = previous?.stage === 'returning' ? previous.drawAt ?? previous.at : e.drawAt;
                e.returnStart = start + Math.floor((e.drawAt - start) / returnMs) * returnMs;
            }
            if (e.finished) schedules.delete(e.requestId);
            else schedules.set(e.requestId, e);
            history.push(e);
        }
        offset = batch.serverTime - Date.now();
        connected = true;
        trim(batch.serverTime);
        dirty = true;
    };
    feed.onerror = () => {
        stoppedAt = clock();
        connected = false;
        dirty = true;
    };

    function freeze(at, playing) {
        frozen = { baseline: new Map(baseline), history: history.slice(), at, until: clock(), started: performance.now(), playing };
        dirty = true;
    }
    function playbackTime(now) {
        if (!frozen) return clock();
        if (!frozen.playing) return frozen.at;
        return Math.min(frozen.until, frozen.at + now - frozen.started);
    }
    document.getElementById('traffic-back').onclick = () => freeze(Math.max(floor, clock() - 5000), true);
    document.getElementById('traffic-live').onclick = () => { frozen = null; dirty = true; };
    pause.onclick = () => {
        if (!frozen) freeze(clock(), false);
        else if (frozen.playing) {
            frozen.at = playbackTime(performance.now());
            frozen.playing = false;
        } else if (frozen.at < frozen.until) {
            frozen.started = performance.now();
            frozen.playing = true;
        } else frozen = null;
        dirty = true;
    };
    picker.onchange = () => { dirty = true; controlsChanged = true; };

    const questionButton = document.getElementById('traffic-question');
    const modal = document.getElementById('liveInferenceFullMessageModal');
    const messageLabels = document.getElementById('live-inference').dataset;
    let questionLoad;
    async function showQuestion(requestId) {
        questionLoad?.abort();
        const load = new AbortController();
        questionLoad = load;
        const content = document.getElementById('liveInferenceFullMessageContent');
        document.getElementById('liveInferenceFullMessageModalLabel').textContent = messageLabels.questionTitle;
        content.textContent = labels.loadingQuestion;
        for (const route of svg.querySelectorAll('.backend-route[aria-describedby]'))
            bootstrap.Tooltip.getInstance(route)?.hide();
        bootstrap.Modal.getOrCreateInstance(modal).show();
        const url = new URL(controls.dataset.questionUrl, location.href);
        url.searchParams.set('requestId', requestId);
        try {
            const response = await fetch(url, { signal: load.signal });
            const text = response.ok ? (await response.json() || messageLabels.noContent)
                : response.status === 404 ? labels.questionUnavailable : labels.questionLoadFailed;
            if (!load.signal.aborted) content.textContent = text;
        } catch {
            if (!load.signal.aborted) content.textContent = labels.questionLoadFailed;
        }
    }
    modal.addEventListener('hide.bs.modal', () => questionLoad?.abort());
    questionButton.onclick = () => showQuestion(picker.value);

    // Capture the request on contact. A moving or grouped marker can change before release.
    let pressed;
    layer.addEventListener('pointerdown', event => {
        const marker = event.target.closest('[data-request-id]');
        if (!marker || !event.isPrimary || event.button !== 0) return;
        pressed = { id: marker.dataset.requestId, x: event.clientX, y: event.clientY };
        layer.setPointerCapture(event.pointerId);
    });
    layer.addEventListener('pointercancel', () => { pressed = null; });
    layer.addEventListener('click', event => {
        const tap = pressed;
        pressed = null;
        if (tap && Math.hypot(event.clientX - tap.x, event.clientY - tap.y) <= 12) showQuestion(tap.id);
        else if (!event.detail && event.target.closest('[data-request-id]'))
            showQuestion(event.target.closest('[data-request-id]').dataset.requestId);
    });
    layer.addEventListener('keydown', event => {
        if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault();
            if (!event.repeat) showQuestion(event.target.dataset.requestId);
        }
    });

    function inLayer(element, point) {
        const matrix = layer.getCTM().inverse().multiply(element.getCTM());
        return new DOMPoint(point.x, point.y).matrixTransform(matrix);
    }
    function onPath(id, fraction) {
        const edge = paths.get(id);
        if (!edge) return null;
        return inLayer(edge.path, edge.path.getPointAtLength(edge.length * fraction));
    }
    // Include the short crossings through nodes and travel at a uniform speed along the route.
    const journeys = new Map();
    function along(ids, progress) {
        const key = ids.join('/');
        let journey = journeys.get(key);
        if (!journey) {
            const segments = [];
            let previousEnd;
            let length = 0;
            for (const id of ids) {
                const edge = paths.get(id);
                if (!edge) return null;
                const start = onPath(id, 0);
                if (previousEnd) {
                    const from = previousEnd;
                    length += Math.hypot(start.x - from.x, start.y - from.y);
                    segments.push({ end: length, from, to: start });
                }
                length += edge.length;
                segments.push({ end: length, id });
                previousEnd = onPath(id, 1);
            }
            journey = { segments, length };
            journeys.set(key, journey);
        }
        const distance = Math.max(0, Math.min(1, progress)) * journey.length;
        let start = 0;
        for (const segment of journey.segments) {
            if (distance <= segment.end) {
                const fraction = (distance - start) / (segment.end - start || 1);
                if (segment.id) return onPath(segment.id, fraction);
                return { x: segment.from.x + (segment.to.x - segment.from.x) * fraction,
                    y: segment.from.y + (segment.to.y - segment.from.y) * fraction };
            }
            start = segment.end;
        }
    }
    function position(e, now) {
        const incoming = ['entry', 'route_' + e.modelId, 'policy_' + e.modelId];
        const dispatch = 'dispatch_' + e.backendId;
        const age = Math.max(0, now - (e.drawAt ?? e.at));
        if (e.stage === 'queued' || (e.stage === 'cancelled' && e.previousStage === 'queued')) {
            const rect = clusters.get('PhysPool_P' + e.providerId)?.querySelector('rect');
            if (rect) {
                const box = rect.getBBox();
                return inLayer(rect, { x: box.x + 8, y: box.y + 27 });
            }
        }
        if (e.stage === 'completed' || e.stage === 'returning') {
            const edges = e.backendId ? [...incoming, dispatch] : incoming;
            const progress = e.stage === 'completed' ? Math.min(1, (now - e.returnStart) / returnMs) : (age % returnMs) / returnMs;
            return along(edges, reduced.matches ? 0 : 1 - progress);
        }
        if (e.stage === 'received') return along(incoming, reduced.matches ? 1 : Math.min(1, age / dwell.received));
        if (e.stage === 'selected') return onPath(dispatch, reduced.matches ? 0 : Math.min(1, age / dwell.selected));
        return e.backendId ? onPath(dispatch, 1) : onPath(incoming[1], 1);
    }

    function marker(key, e, count, point, selected) {
        let g = groups.get(key);
        if (!g) {
            g = document.createElementNS(ns, 'g');
            g.setAttribute('role', 'button');
            g.setAttribute('tabindex', '0');
            g.setAttribute('aria-haspopup', 'dialog');
            const make = (tag, name) => {
                const el = document.createElementNS(ns, tag);
                el.classList.add(name);
                return el;
            };
            const halo = make('circle', 'marker-halo');
            halo.setAttribute('r', '17');
            const dot = make('circle', 'marker-dot');
            dot.setAttribute('r', '11');
            const arrow = make('path', 'marker-direction');
            arrow.setAttribute('d', 'M -5 0 H 5 M 1 -4 L 5 0 L 1 4');
            const badge = make('g', 'marker-badge');
            const plate = make('rect', 'marker-plate');
            plate.setAttribute('rx', '6');
            const caption = make('text', 'marker-caption');
            badge.append(plate, caption);
            g.append(halo, dot, arrow, badge);
            layer.append(g);
            groups.set(key, g);
        }
        g.dataset.stage = e.stage;
        g.dataset.requestId = e.requestId;
        g.setAttribute('aria-label', questionButton.textContent + ' · ' + e.questionPreview);
        g.classList.toggle('followed', selected);
        g.setAttribute('transform', 'translate(' + point.x + ' ' + point.y + ')');
        const queued = e.stage === 'queued';
        const returning = e.stage === 'returning' || e.stage === 'completed';
        const preview = returning ? e.answerPreview : e.questionPreview;
        const countText = queued ? labels.queued + ' · ' + count : count > 1 ? '×' + count : '';
        const label = [preview, countText].filter(Boolean).join('  ·  ');
        const badge = g.lastChild;
        const caption = badge.lastChild;
        if (caption.textContent !== label || !badge.hasAttribute('data-width')) {
            caption.textContent = label;
            const width = Math.ceil(caption.getComputedTextLength()) + 20;
            badge.dataset.width = width;
            badge.firstChild.setAttribute('width', width);
            badge.firstChild.setAttribute('height', '28');
            caption.setAttribute('x', '10');
            caption.setAttribute('y', '20');
            badge.style.visibility = label ? 'visible' : 'hidden';
        }
        const width = Number(badge.dataset.width);
        const box = svg.viewBox.baseVal;
        const x = Math.max(box.x + 2, Math.min(box.x + box.width - width - 2,
            point.x - (queued ? width + 20 : width / 2)));
        const y = queued ? -14 : point.y - 47 < box.y ? 19 : -47;
        badge.setAttribute('transform', 'translate(' + (x - point.x) + ' ' + y + ')');
    }
    function updateList(items, events, time) {
        const selected = picker.value;
        const recent = new Map(items);
        for (const e of events) if (e.at <= time) recent.set(e.requestId, e);
        const options = [...recent.values()].sort((a, b) => Number(a.finished) - Number(b.finished) || b.sequence - a.sequence);
        const fragment = document.createDocumentFragment();
        for (const e of options.slice(0, 150).concat(options.slice(150).filter(e => e.requestId === selected))) {
            fragment.append(new Option((modelNames.get(e.modelId) ?? e.modelId) + ' · ' + e.requestId.slice(0, 8) + ' · ' + labels[e.stage], e.requestId));
        }
        picker.replaceChildren(picker.options[0], fragment);
        picker.value = recent.has(selected) ? selected : '';
        detail.hidden = !picker.value;
        questionButton.hidden = !picker.value;
        if (!detail.hidden) {
            const trace = events.filter(e => e.requestId === selected && e.at <= time && !e.previewUpdate);
            const first = (frozen?.baseline ?? baseline).get(selected);
            if (first && first.at <= time) trace.unshift(first);
            detail.textContent = trace.map(e => new Date(e.at).toLocaleTimeString() + ' ' + labels[e.stage] +
                (e.stage === 'selected' ? ' #' + e.attempt + ' · ' + (routes.get(e.backendId)?.textContent ?? e.backendId) : '')).join(' → ');
            const last = recent.get(selected);
            if (!last.finished) detail.textContent += ' · ' + Math.floor(Math.max(0, time - (last.stageAt ?? last.at)) / 1000) + ' s';
        }
    }

    function draw(now) {
        frame = requestAnimationFrame(draw);
        if (document.hidden || (!dirty && now - lastPaint < (reduced.matches ? 300 : 50))) return;
        lastPaint = now;
        if (frozen?.playing && playbackTime(now) >= frozen.until) frozen = null;
        const time = playbackTime(now);
        const source = frozen ?? { baseline, history };
        const state = new Map(source.baseline);
        const displayed = new Map(source.baseline);
        for (const e of source.history) {
            if (e.at > time) break;
            if (e.drawAt <= time || reduced.matches) {
                const expires = e.stage === 'completed' && !reduced.matches
                    ? e.returnStart + returnMs + arrivalHold : e.drawAt + arrivalHold;
                if (e.finished && time > expires) displayed.delete(e.requestId);
                else displayed.set(e.requestId, e);
            }
            apply(state, e);
        }
        const activeNodes = new Set();
        for (const e of state.values()) {
            activeNodes.add(nodes.get('Svc_' + e.modelId));
            if (['sent', 'headers', 'returning'].includes(e.stage))
                activeNodes.add(nodes.get(routes.get(e.backendId)?.dataset.physical));
        }
        for (const node of highlighted) if (!activeNodes.has(node)) {
            node.classList.remove('traffic-active');
            const health = node.querySelector('.topology-health');
            if (health?.dataset.idle) health.textContent = health.dataset.idle;
            highlighted.delete(node);
        }
        for (const node of activeNodes) if (node && !highlighted.has(node)) {
            node.classList.add('traffic-active');
            const health = node.querySelector('.topology-health');
            if (health && !node.classList.contains('banned')) {
                health.dataset.idle = health.textContent;
                health.textContent = labels.busy;
            }
            highlighted.add(node);
        }
        const buckets = new Map();
        for (const e of displayed.values()) {
            const key = e.stage === 'queued' ? 'queue-' + e.providerId :
                e.requestId === picker.value ? e.requestId : [e.stage, e.modelId, e.backendId].join('-');
            const bucket = buckets.get(key);
            if (bucket) {
                bucket.count++;
                if (e.requestId === picker.value) bucket.e = e;
            }
            else buckets.set(key, { e, count: 1 });
        }
        for (const [key, g] of groups) if (!buckets.has(key)) { g.remove(); groups.delete(key); }
        for (const [key, { e, count }] of buckets) {
            const point = position(e, time);
            if (point) marker(key, e, count, point, e.requestId === picker.value);
        }
        if (controlsChanged || now - lastList >= 350) {
            trim(clock());
            updateList(state, source.history, time);
            status.textContent = !connected ? labels.reconnecting : frozen ? labels[frozen.playing ? 'replay' : 'paused'] : labels.live;
            status.classList.toggle('bg-secondary', !connected || !!frozen);
            status.classList.toggle('bg-success', connected && !frozen);
            pause.setAttribute('aria-pressed', String(!!frozen && !frozen.playing));
            lastList = now;
            controlsChanged = false;
        }
        dirty = false;
    }
    let lastPaint = 0;
    frame = requestAnimationFrame(draw);
    window.addEventListener('pagehide', event => {
        if (!event.persisted) { feed.close(); cancelAnimationFrame(frame); }
    });
}
