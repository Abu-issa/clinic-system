"use strict";
window.addEventListener("pageshow", event => {
    // A history-restored page must obtain current state/token; only a GET is repeated.
    if (event.persisted) { window.location.reload(); return; }
    document.querySelectorAll("form[data-single-submit]").forEach(form => {
        delete form.dataset.submitting;
        form.querySelectorAll("button[type=submit]").forEach(button => { button.disabled = false; });
    });
});
document.querySelectorAll("form[data-single-submit]").forEach(form => {
    form.addEventListener("submit", event => {
        if (form.dataset.submitting) { event.preventDefault(); return; }
        form.dataset.submitting = "true";
        form.querySelectorAll("button[type=submit]").forEach(button => { button.disabled = true; });
    });
});
document.querySelectorAll("form[data-upload]").forEach(form => {
    const button = form.querySelector("[data-upload-button]");
    const error = form.querySelector("[data-upload-error]");
    const label = button.textContent;
    form.addEventListener("submit", async event => {
        event.preventDefault();
        if (button.disabled) return;
        button.disabled = true;
        button.textContent = form.dataset.sending;
        error.textContent = "";
        try {
            const response = await fetch(form.action, {
                method: "POST", credentials: "same-origin", redirect: "manual",
                headers: { "X-CSRF-TOKEN": form.querySelector('[name="__RequestVerificationToken"]').value },
                body: new FormData(form)
            });
            // Do not follow the GET inside fetch: the browser navigation performs the one audited read.
            if (response.type === "opaqueredirect" || response.status === 302 || response.status === 303) {
                window.location.assign(form.dataset.returnUrl);
                return;
            }
            // Safe HTML from our error renderer; never insert a response body into the live document.
            const messages = { 400: form.dataset.csrf, 401: form.dataset.signin, 403: form.dataset.denied,
                404: form.dataset.unavailable, 413: form.dataset.large };
            error.textContent = messages[response.status] || form.dataset.error;
        } catch {
            error.textContent = form.dataset.error;
        }
        button.textContent = label;
        button.disabled = false;
    });
    form.querySelector("[data-upload-unavailable]").hidden = true;
    button.disabled = false;
});
