(() => {
    const root = document.getElementById('live-inference');
    if (!root) return;
    const label = (name, fallback) => root.dataset[name] || fallback;
    const rows = root.querySelector('[data-live-rows]');
    const template = root.querySelector('[data-live-row]');
    const connection = root.querySelector('[data-connection-status]');
    const modal = document.getElementById('liveInferenceFullMessageModal');
    let state = JSON.parse(root.dataset.initialState);
    let clockOffset = 0;
    let socket;
    let retry;
    let retryDelay = 1000;
    let disposed = false;

    function updateTimes() {
        rows.querySelectorAll('[data-started]').forEach(cell => {
            const seconds = Math.max(0, Math.floor((Date.now() + clockOffset - Date.parse(cell.dataset.started)) / 1000));
            const elapsed = seconds < 60 ? `${seconds}s` : `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
            cell.textContent = `${label('running', 'Running for')} ${elapsed}`;
        });
    }

    function render(next) {
        state = next;
        clockOffset = Date.parse(state.serverTime) - Date.now();
        const fragment = document.createDocumentFragment();
        for (const item of state.items) {
            const row = template.content.firstElementChild.cloneNode(true);
            row.dataset.model = item.modelName;
            row.classList.toggle('text-muted', !item.isActive);
            const field = name => row.querySelector(`[data-field="${name}"]`);
            field('status').textContent = item.isActive ? label('busy', 'Busy') : label('idle', 'Idle');
            field('status').className = `badge ${item.isActive ? 'bg-danger' : 'bg-secondary'}`;
            field('model').textContent = item.modelName;
            field('model').classList.toggle('fw-bold', item.isActive);
            field('backend').textContent = item.backendModelName;
            field('count').textContent = item.concurrentCount;
            field('count').className = `badge ${item.isActive ? 'bg-warning text-dark' : 'bg-secondary'}`;
            field('key').textContent = item.apiKeyName || label('anonymous', '(anonymous)');
            field('question').textContent = item.lastQuestion ? item.lastQuestion + (item.fullQuestion.length > 30 ? '…' : '') : label('noContent', '(no content)');
            field('question').disabled = !item.fullQuestion;
            // An in-flight request never displays an answer from a previous request.
            const answer = item.isActive ? '' : item.errorMessage || item.answer;
            field('answer').textContent = answer.length > 30 ? answer.slice(0, 30) + '…' : answer;
            field('answer').hidden = !answer;
            field('answer').classList.toggle('text-danger', Boolean(item.errorMessage));
            if (item.isActive) {
                field('time').dataset.started = item.lastStartedAt;
            } else if (item.lastCompletedAt) {
                const timeLabel = document.createElement('label');
                timeLabel.dataset.utcTime = item.lastCompletedAt;
                timeLabel.className = 'mb-0';
                timeLabel.textContent = new Date(item.lastCompletedAt).toLocaleString();
                field('time').append(document.createTextNode(label('last', 'Last at') + ' '), timeLabel);
            }
            fragment.append(row);
        }
        rows.replaceChildren(fragment);
        root.querySelector('[data-live-empty]').hidden = state.items.length !== 0;
        const count = root.querySelector('[data-active-count]');
        count.textContent = state.activeCount ? `${state.activeCount} ${label('active', 'Active')}` : label('idle', 'Idle');
        count.className = `badge ${state.activeCount ? 'bg-danger' : 'bg-secondary'}`;
        updateTimes();
    }

    rows.addEventListener('click', event => {
        const button = event.target.closest('button[data-field]');
        if (!button) return;
        const item = state.items.find(value => value.modelName === button.closest('tr').dataset.model);
        if (!item) return;
        const isQuestion = button.dataset.field === 'question';
        document.getElementById('liveInferenceFullMessageModalLabel').textContent = isQuestion ? label('questionTitle', 'Full Request Message') : label('answerTitle', 'Answer or error');
        document.getElementById('liveInferenceFullMessageContent').textContent = isQuestion ? item.fullQuestion : item.errorMessage || item.answer;
        bootstrap.Modal.getOrCreateInstance(modal).show();
    });

    function connect() {
        if (disposed) return;
        connection.textContent = retryDelay > 1000 ? label('reconnecting', 'Reconnecting…') : label('connecting', 'Connecting…');
        const url = new URL(root.dataset.socketUrl, location.href);
        url.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
        socket = new WebSocket(url);
        socket.onmessage = event => {
            render(JSON.parse(event.data));
            retryDelay = 1000;
            connection.textContent = '';
        };
        socket.onerror = () => socket.close();
        socket.onclose = () => {
            if (disposed) return;
            connection.textContent = label('reconnecting', 'Reconnecting…');
            retry = setTimeout(connect, retryDelay);
            retryDelay = Math.min(retryDelay * 2, 30000);
        };
    }

    render(state);
    const timer = setInterval(updateTimes, 1000);
    connect();
    window.addEventListener('pagehide', () => {
        disposed = true;
        clearInterval(timer);
        clearTimeout(retry);
        socket?.close();
    });
    window.addEventListener('pageshow', event => {
        if (event.persisted) location.reload();
    });
})();
