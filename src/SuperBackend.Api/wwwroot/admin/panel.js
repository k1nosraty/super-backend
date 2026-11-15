import { locales } from "./locales.js";

const $ = id => document.getElementById(id);
const admin = "/api/v1/admin";
const auth = "/api/v1/auth";
const pageSize = 20;
const state = { language: navigator.language.startsWith("fa") ? "fa" : "en", view: "users", offset: 0, search: "", cursors: [null] };
let session;
let refresh;
let renderId = 0;

const t = key => locales[state.language][key] ?? key;
const enc = encodeURIComponent;
const node = (tag, text, className) => {
    const item = document.createElement(tag);
    if (text != null) item.textContent = text;
    if (className) item.className = className;
    return item;
};

function notice(target, message) {
    target.textContent = message || "";
    target.hidden = !message;
}

function applyLanguage() {
    document.documentElement.lang = state.language;
    document.documentElement.dir = state.language === "fa" ? "rtl" : "ltr";
    for (const item of document.querySelectorAll("[data-text]")) item.textContent = t(item.dataset.text);
    for (const item of document.querySelectorAll("[data-label]")) item.setAttribute("aria-label", t(item.dataset.label));
}

function saveSession(tokens) {
    session = { ...tokens, expiresAt: Date.now() + tokens.expiresIn * 1000 };
}

function showLogin(message) {
    session = undefined;
    refresh = undefined;
    renderId++;
    if ($("detail").open) $("detail").close();
    for (const id of ["toolbar", "display", "paging", "detail-body"]) $(id).replaceChildren();
    $("workspace").hidden = true;
    $("logout").hidden = true;
    $("login-screen").hidden = false;
    $("login-form").reset();
    notice($("login-error"), message);
}

async function request(path, method = "GET", body, token) {
    const headers = { "Accept": "application/json, application/problem+json", "Accept-Language": state.language };
    if (body !== undefined) headers["Content-Type"] = "application/json";
    if (token) headers.Authorization = "Bearer " + token;

    const response = await fetch(path, {
        method,
        headers,
        credentials: "omit",
        cache: "no-store",
        body: body === undefined ? undefined : JSON.stringify(body)
    });
    if (!response.ok) {
        const problem = await response.json().catch(() => null);
        const error = new Error(problem?.title || t("requestFailed"));
        error.status = response.status;
        error.detail = problem?.detail;
        throw error;
    }
    return response.status === 204 ? undefined : response.json();
}

async function call(path, method, body) {
    if (session && Date.now() > session.expiresAt - 30_000) {
        const expiring = session;
        refresh ??= request(auth + "/refresh", "POST", { refreshToken: expiring.refreshToken })
            .then(tokens => { if (session === expiring) saveSession(tokens); })
            .catch(error => { if (session === expiring) showLogin(t("expired")); throw error; })
            .finally(() => { refresh = undefined; });
        await refresh;
    }
    if (!session) throw new Error(t("expired"));
    try {
        return await request(admin + path, method, body, session.accessToken);
    } catch (error) {
        if (error.status === 401) showLogin(t("expired"));
        throw error;
    }
}

$("login-form").addEventListener("submit", async event => {
    event.preventDefault();
    const form = event.currentTarget;
    const submit = form.querySelector("[type=submit]");
    const fields = Object.fromEntries(new FormData(form));
    submit.disabled = true;
    notice($("login-error"));
    try {
        saveSession(await request(auth + "/login", "POST", fields));
        await call("/permissions");
        form.reset();
        $("login-screen").hidden = true;
        $("workspace").hidden = false;
        $("logout").hidden = false;
        setView("users");
    } catch (error) {
        session = undefined;
        form.elements.password.value = "";
        if (error.detail === "RequiresTwoFactor") $("mfa").open = true;
        const message = error.status === 401 ? t("wrongLogin") : error.status === 403 ? t("noAccess") : error.message;
        notice($("login-error"), message);
    } finally {
        submit.disabled = false;
    }
});

$("logout").addEventListener("click", async () => {
    const current = session;
    try {
        if (current)
            await request(auth + "/logout", "POST", undefined, current.accessToken);
        showLogin();
    } catch (error) {
        if (error.status === 401)
            showLogin();
        else
            notice($("workspace-error"), error.message);
    }
});
$("language").value = state.language;
$("language").addEventListener("change", event => {
    state.language = event.currentTarget.value;
    applyLanguage();
    if ($("detail").open) $("detail").close();
    if (session) render();
});
for (const button of document.querySelectorAll("[data-view]")) {
    button.addEventListener("click", () => setView(button.dataset.view));
}
applyLanguage();

function setView(view) {
    state.view = view;
    state.offset = 0;
    state.cursors = [null];
    render();
    $("view-title").focus();
}

function button(key, handler, className = "secondary") {
    const item = node("button", t(key), className);
    item.type = "button";
    item.addEventListener("click", handler);
    return item;
}

function toolbar() {
    const container = $("toolbar");
    container.replaceChildren();
    if (state.view === "audit") return;
    const form = node("form", null, "actions");
    const label = node("label");
    const input = node("input");
    const search = state.view === "users";
    input.name = search ? "search" : "name";
    input.type = search ? "search" : "text";
    input.value = search ? state.search : "";
    input.maxLength = search ? 256 : 100;
    input.required = !search;
    label.append(node("span", t(search ? "search" : "name"), "sr-only"), input);
    input.placeholder = t(search ? "search" : "name");
    const submit = node("button", t(search ? "find" : "create"));
    form.append(label, submit);
    if (search) form.append(button("invite", inviteUser));
    form.addEventListener("submit", async event => {
        event.preventDefault();
        if (search) {
            state.search = input.value.trim();
            state.offset = 0;
            render();
            return;
        }
        submit.disabled = true;
        try {
            await call("/roles", "POST", { name: input.value });
            state.offset = 0;
            render();
        } catch (error) {
            notice($("workspace-error"), error.message);
            submit.disabled = false;
        }
    });
    container.append(form);
}

async function render() {
    const id = ++renderId;
    const view = state.view;
    $("view-title").textContent = t(view);
    for (const item of document.querySelectorAll("[data-view]")) {
        if (item.dataset.view === view) item.setAttribute("aria-current", "page");
        else item.removeAttribute("aria-current");
    }
    notice($("workspace-error"));
    $("display").replaceChildren();
    $("paging").replaceChildren();
    $("result-status").textContent = t("loading");
    toolbar();
    try {
        const cursor = state.cursors.at(-1);
        const query = new URLSearchParams({ limit: pageSize + 1 });
        if (view === "audit") {
            if (cursor) query.set("before", cursor);
        } else {
            query.set("offset", state.offset);
            if (view === "users" && state.search) query.set("search", state.search);
        }
        const results = await call("/" + view + "?" + query);
        if (id !== renderId) return;
        const rows = results.slice(0, pageSize);
        renderTable(view, rows);
        $("result-status").textContent = rows.length ? t("loaded") : t("none");
        const paging = $("paging");
        if (state.offset || state.cursors.length > 1) paging.append(button("previous", () => {
            if (view === "audit") state.cursors.pop();
            else state.offset -= pageSize;
            render();
        }));
        if (results.length > pageSize) paging.append(button("next", () => {
            if (view === "audit") state.cursors.push(rows.at(-1).id);
            else state.offset += pageSize;
            render();
        }));
    } catch (error) {
        if (id === renderId) {
            $("result-status").textContent = "";
            notice($("workspace-error"), error.message);
        }
    }
}

function renderTable(view, rows) {
    if (!rows.length) {
        $("display").append(node("p", t("none"), "empty"));
        return;
    }
    const headings = view === "users" ? ["email", "name", "status", "verified", "manage"]
        : view === "roles" ? ["name", "permissions"] : ["occurred", "action", "actor", "target"];
    const wrapper = node("div", null, "table-wrap");
    wrapper.tabIndex = 0;
    wrapper.setAttribute("role", "region");
    wrapper.setAttribute("aria-label", t(view));
    const table = node("table", null, view === "audit" ? "audit-table" : undefined);
    const header = table.createTHead().insertRow();
    for (const key of headings) {
        const cell = node("th", t(key));
        cell.scope = "col";
        header.append(cell);
    }
    const body = table.createTBody();
    const date = new Intl.DateTimeFormat(state.language, { dateStyle: "medium", timeStyle: "short" });
    for (const item of rows) {
        const row = body.insertRow();
        const items = view === "users"
            ? [item.email, item.displayName, t(item.isDisabled ? "disabled" : "enabled"), t(item.emailConfirmed ? "yes" : "no"),
                button("manage", () => manageUser(item))]
            : view === "roles" ? [item.name, button("permissions", () => manageRole(item))]
                : [date.format(new Date(item.occurredAt)), t(item.action), item.actorId,
                    [item.userId, item.roleId, item.permission].filter(Boolean).join(" / ")];
        for (const item of items) {
            const cell = row.insertCell();
            cell.dir = "auto";
            if (item instanceof HTMLElement) cell.append(item);
            else cell.textContent = item || "—";
        }
    }
    wrapper.append(table);
    $("display").append(wrapper);
}

function openDetail(title) {
    $("detail-title").textContent = title;
    const content = node("div");
    $("detail-body").replaceChildren(content);
    notice($("detail-error"));
    $("detail").showModal();
    return content;
}

function controls(title, items, selected, save) {
    const list = node("div", null, "checks");
    list.append(node("h2", title));
    if (!items.length) list.append(node("p", t("none")));
    for (const entry of items) {
        const label = node("label");
        const input = node("input");
        input.type = "checkbox";
        input.checked = selected.has(entry.id);
        input.disabled = entry.disabled ?? false;
        input.addEventListener("change", async () => {
            input.disabled = true;
            notice($("detail-error"));
            try {
                await save(entry.id, input.checked);
            } catch (error) {
                input.checked = !input.checked;
                notice($("detail-error"), error.message);
            } finally {
                input.disabled = false;
            }
        });
        label.append(input, node("span", entry.name));
        list.append(label);
    }
    return list;
}

async function roles() {
    const result = [];
    let page;
    do {
        page = await call("/roles?limit=100&offset=" + result.length);
        result.push(...page);
    } while (page.length === 100);
    return result;
}

function inviteUser() {
    const body = openDetail(t("invite"));
    const sent = node("p");
    sent.setAttribute("role", "status");
    body.append(editForm("email", "send", "", async email => {
        await call("/invitations", "POST", { email });
        sent.textContent = t("invited");
    }, "email"), sent);
}

function editForm(labelKey, submitKey, value, onSave, type = "text") {
    const form = node("form", null, "actions");
    const label = node("label", t(labelKey));
    const input = node("input");
    input.type = type;
    input.value = value;
    input.required = true;
    input.maxLength = type === "email" ? 256 : 100;
    label.append(input);
    const submit = node("button", t(submitKey));
    form.append(label, submit);
    form.addEventListener("submit", event => {
        event.preventDefault();
        mutate(submit, () => onSave(input.value.trim()));
    });
    return form;
}

async function manageUser(user) {
    const body = openDetail(user.email || user.id);
    if (user.isAdministrator) {
        body.append(node("p", t("reserved")));
        return;
    }
    const route = "/users/" + enc(user.id);
    const actions = node("div", null, "actions");
    const status = button(user.isDisabled ? "enable" : "disable", async () => {
        if (!user.isDisabled && !confirm(t("confirmDisable"))) return;
        await mutate(status, async () => {
            await call(route + "/status", "PUT", { disabled: !user.isDisabled });
            user.isDisabled = !user.isDisabled;
            status.textContent = t(user.isDisabled ? "enable" : "disable");
            status.classList.toggle("danger", !user.isDisabled);
            $("result-status").textContent = t("accountSaved");
            render();
        });
    }, user.isDisabled ? "secondary" : "secondary danger");
    const revoke = button("revoke", async () => {
        if (!confirm(t("confirmRevoke"))) return;
        await mutate(revoke, async () => {
            await call(route + "/revoke-sessions", "POST");
            $("result-status").textContent = t("sessionsRevoked");
        });
    });
    actions.append(status, revoke);
    body.append(actions);
    try {
        const [catalog, current] = await Promise.all([roles(), call(route + "/roles")]);
        body.append(controls(t("memberships"), catalog.filter(role => role.name !== "Administrator"),
            new Set(current.map(role => role.id)),
            (id, checked) => call(route + "/roles/" + enc(id), checked ? "PUT" : "DELETE")));
    } catch (error) {
        notice($("detail-error"), error.message);
    }
}

async function manageRole(role) {
    const body = openDetail(role.name);
    if (role.name === "Administrator") {
        body.append(node("p", t("reserved")));
        return;
    }
    const resource = "/roles/" + enc(role.id);
    const route = resource + "/permissions";
    body.append(editForm("name", "save", role.name, async name => {
        await call(resource, "PUT", { name });
        $("detail-title").textContent = name;
        render();
    }));
    const remove = button("delete", async () => {
        if (!confirm(t("confirmDelete"))) return;
        await mutate(remove, async () => {
            await call(resource, "DELETE");
            $("detail").close();
            setView("roles");
        });
    }, "secondary danger");
    body.append(node("div", null, "actions"));
    body.lastChild.append(remove);
    try {
        const [catalog, current] = await Promise.all([call("/permissions"), call(route)]);
        body.append(controls(t("permissions"), catalog.map(item => ({ id: item.name, name: item.name + " · " + item.description })),
            new Set(current), (id, checked) => call(route + "/" + enc(id), checked ? "PUT" : "DELETE")));
    } catch (error) {
        notice($("detail-error"), error.message);
    }
}

async function mutate(control, action) {
    control.disabled = true;
    notice($("detail-error"));
    try {
        await action();
    } catch (error) {
        notice($("detail-error"), error.message);
    } finally {
        control.disabled = false;
    }
}
