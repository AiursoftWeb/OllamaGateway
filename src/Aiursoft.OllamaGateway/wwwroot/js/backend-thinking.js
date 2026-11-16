document.querySelectorAll('.backend-settings').forEach(function (container) {
    const protocol = container.querySelector('.backend-protocol');
    const thinking = container.querySelector('.backend-thinking-override');
    const openAiThinking = container.querySelector('.backend-openai-thinking-override');
    const effort = container.querySelector('.backend-reasoning-override');
    if (!protocol || !thinking || !openAiThinking || !effort) return;

    function updateThinkingControl() {
        const isOllama = protocol.value === '0';
        thinking.hidden = !isOllama;
        thinking.disabled = !isOllama;
        openAiThinking.hidden = isOllama;
        openAiThinking.disabled = isOllama;
        effort.hidden = isOllama;
        effort.disabled = isOllama;
        container.querySelectorAll('.backend-effort-settings').forEach(el => el.hidden = isOllama);
        container.querySelectorAll('.backend-effort-unavailable').forEach(el => el.hidden = !isOllama);
        if (isOllama) {
            effort.value = '';
            openAiThinking.value = '';
        } else thinking.value = '';
    }

    protocol.addEventListener('change', updateThinkingControl);
    updateThinkingControl();
});
