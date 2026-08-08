// On-screen keypad for the kids' PIN screen. Plain DOM so it works on the
// static-rendered sign-in page, where there is no Blazor circuit yet.
document.addEventListener('click', (event) => {
    const key = event.target.closest('[data-digit], [data-clear]');
    if (!key) return;
    const input = document.getElementById('kid-pin');
    if (!input) return;

    if (key.hasAttribute('data-clear')) {
        input.value = '';
    } else if (input.value.length < 8) {
        input.value += key.getAttribute('data-digit');
    }
    // Blazor's SSR form reads the DOM value on submit, but fire the event anyway
    // so anything else bound to the input stays in step.
    input.dispatchEvent(new Event('input', { bubbles: true }));
    input.dispatchEvent(new Event('change', { bubbles: true }));
});
