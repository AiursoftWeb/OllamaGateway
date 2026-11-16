document.querySelectorAll('.backend-settings').forEach(function (container) {
    const protocol = container.querySelector('.backend-protocol');
    const thinking = container.querySelector('.backend-thinking-override');
    const passthrough = container.querySelector('.backend-thinking-passthrough');
    if (!protocol || !thinking || !passthrough) return;

    function updateThinkingControl() {
        const isOllama = protocol.value === '0';
        thinking.hidden = !isOllama;
        thinking.disabled = !isOllama;
        passthrough.hidden = isOllama;
        if (!isOllama) thinking.value = '';
    }

    protocol.addEventListener('change', updateThinkingControl);
    updateThinkingControl();
});
