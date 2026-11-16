document.querySelectorAll('.backend-settings').forEach(function (container) {
    const protocol = container.querySelector('.backend-protocol');
    const thinking = container.querySelector('.backend-thinking-override');
    const effort = container.querySelector('.backend-reasoning-override');
    if (!protocol || !thinking || !effort) return;

    function updateThinkingControl() {
        const isOllama = protocol.value === '0';
        thinking.hidden = !isOllama;
        thinking.disabled = !isOllama;
        effort.hidden = isOllama;
        effort.disabled = isOllama;
        if (isOllama) effort.value = '';
        if (!isOllama) thinking.value = '';
    }

    protocol.addEventListener('change', updateThinkingControl);
    updateThinkingControl();
});
