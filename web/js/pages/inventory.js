// Inventory: everything the last memory scan (or imported file) holds - resources, mods, arcanes,
// relics, parts, fish, decorations, equipment - searchable, with the game's own icons.

import { $, act, api, artUrl, debounce, esc, fmt, ago } from "../lib.js";
import * as drawer from "../drawer.js";

const st = { q: "", kind: "", sort: "name", tradable: false, weapons: false, mastered: false, crafting: "", limit: 240 };
// Kinds the details panel knows; the rest have nothing more to show.
const DETAILS = new Set(["Warframes", "Primary", "Secondary", "Melee", "Sentinels", "Archwing", "Arch-Gun", "Arch-Melee",
  "Pets", "Misc", "Prime parts", "Parts", "Relics"]);
// Equipment kinds from the catalog, gathered under one chip.
const EQUIPMENT = new Set(["Warframes", "Primary", "Secondary", "Melee", "Sentinels", "Archwing", "Arch-Gun", "Arch-Melee",
  "Pets", "Misc", "Weapons", "Amps", "Companions"]);
const group = (kind) => (EQUIPMENT.has(kind) ? "Equipment" : kind);

const SORTS = [["name", "Name"], ["count", "Most held"], ["value", "Value"]];
const value = (x) => (x.price || 0) * x.count;

const pic = (x) => (x.image
  ? `<img class="inv-pic" loading="lazy" decoding="async" alt="" src="${esc(artUrl(x.image, 52))}" onerror="this.replaceWith(Object.assign(document.createElement('span'),{className:'inv-pic none'}))">`
  : `<span class="inv-pic none"></span>`);

function tile(x) {
  const open = x.hasDetail ? (x.lookupDetail ? "lookup:" : "") + x.uid : DETAILS.has(x.kind) ? x.uid : null;
  const money = [
    x.price != null ? `<span class="plat" title="Lowest sell order, each">${fmt(x.price)}</span>` : "",
    x.ducats ? `<span class="duc" title="Ducats each">${fmt(x.ducats)}</span>` : "",
  ].join("");
  const usage = x.craftingUses ? `Used in ${fmt(x.craftingUses)} recipe${x.craftingUses === 1 ? "" : "s"}` : x.craftingKnown ? "No known crafting uses" : "Recipe data unavailable";
  return `<${open ? "button type=\"button\"" : "div"} class="inv-tile${open ? " click" : ""}" ${open ? `data-uid="${esc(open)}"` : ""} title="${esc(x.name)}">
    ${pic(x)}<span class="inv-body"><span class="inv-name">${esc(x.name)}</span>
      <span class="inv-meta"><span class="inv-kind">${esc(x.kind)}</span>${money}</span>
      ${x.isWeapon || x.craftingUses ? `<span class="inv-meta">${x.mastered === true ? "Mastered Â· " : ""}${usage}</span>` : ""}</span>
    <span class="inv-count">${x.count > 1 || !EQUIPMENT.has(x.kind) ? fmt(x.count) : ""}</span></${open ? "button" : "div"}>`;
}

const tabs = [["items", "Items"], ["changes", "Changes since last sync"], ["copies", "Copies to keep"]];
function navigation(tab) {
  return `<nav class="toolbar inventory-tabs" aria-label="Inventory views">${tabs.map(([id, name]) =>
    `<a class="btn ghost" href="#inventory?tab=${id}" ${id === tab ? 'aria-current="page"' : ""}>${name}</a>`).join("")}</nav>`;
}

const goalLink = g => `<a href="#farming?item=${encodeURIComponent("task:" + g.uid)}">${esc(g.name)}</a>`;

function changesPanel(data) {
  if (data.from == null) return `<div class="empty">Changes appear after two consecutive confirmed inventories for this account.</div>`;
  const date = t => esc(new Date(t * 1000).toLocaleString());
  const rows = [...data.currencies, ...data.items.sort((a, b) => a.name.localeCompare(b.name))];
  const table = rows.length ? `<div class="inventory-table"><table><thead><tr><th>Item or currency</th><th>Before</th><th>After</th><th>Change</th></tr></thead><tbody>
    ${rows.map(r => `<tr><td>${esc(r.name)}${(r.goals || []).map(g => `<small>${goalLink(g)}</small>`).join("")}</td><td>${fmt(r.before)}</td><td>${fmt(r.after)}</td><td>${r.delta > 0 ? "+" : ""}${fmt(r.delta)}</td></tr>`).join("")}
    </tbody></table></div>` : '<div class="empty">Item quantities and known currency balances stayed the same.</div>';
  const goals = data.goals.length ? `<h2>Farming progress</h2><div class="list">${data.goals.map(g =>
    `<a class="list-row" href="#farming?item=${encodeURIComponent("task:" + g.uid)}"><b>${esc(g.name)}</b>
       <span>${esc(g.before)} &rarr; ${esc(g.after)}${g.before === g.after && g.beforeHave !== g.afterHave ? ` (${fmt(g.beforeHave)} &rarr; ${fmt(g.afterHave)})` : ""}</span></a>`).join("")}</div>` : "";
  return `<p class="muted">${date(data.from)} &rarr; ${date(data.to)}</p>${data.limited ? '<p class="muted">Coverage differs or is unknown for part of this comparison.</p>' : ""}${table}${goals}`;
}

const copyFilters = { query: "", surplus: false, limit: 80 };
function copiesPanel(view, data, mastered) {
  const form = view.querySelector("[data-copies]");
  form.innerHTML = `<p class="muted">The minimum is how many to keep after crafting your equipment goals.</p>
    ${!data.hasInventory ? '<p class="muted">Owned and unreserved quantities appear after the first confirmed inventory.</p>' : ""}
    ${!data.complete ? '<p class="callout">Some planned recipes are incomplete. Unreserved quantities are unknown.</p>' : ""}
    <div class="toolbar"><input type="search" aria-label="Find an item" placeholder="Find an item" value="${esc(copyFilters.query)}">
    <label class="check"><input type="checkbox" data-surplus ${copyFilters.surplus ? "checked" : ""}>Unreserved copies only</label>
    <label class="check"><input type="checkbox" data-mastered ${mastered ? "checked" : ""}>Mastered weapons only</label></div>
    <div class="inventory-table"><table><thead><tr><th>Item</th><th>Owned</th><th>For crafting</th><th>Keep after crafting</th><th>Unreserved</th></tr></thead>
    <tbody></tbody></table></div><p data-empty hidden>No matching items.</p><button type="button" data-more hidden>Show more</button>`;
  const items = data.copies.sort((a, b) => Number(b.goals.length > 0 || b.minimum > 0) - Number(a.goals.length > 0 || a.minimum > 0) || a.name.localeCompare(b.name));
  function draw() {
    const query = copyFilters.query.trim().toLowerCase();
    const rows = items.filter(r => r.name.toLowerCase().includes(query) && (!copyFilters.surplus || r.spare > 0) && (!mastered || r.masteredWeapon));
    form.querySelector("tbody").innerHTML = rows.slice(0, copyFilters.limit).map(r => `<tr>
      <td>${esc(r.name)}${r.goals.map(g => `<small>${goalLink(g)} &times; ${fmt(g.count)}</small>`).join("")}</td>
      <td>${fmt(r.owned)}</td><td>${r.required == null ? '<span title="Blueprint reusability is unknown">Unknown</span>' : fmt(r.required)}</td><td><form data-uid="${esc(r.uid)}">
      <input type="number" required min="0" max="2147483647" step="1" value="${r.minimum}" aria-label="Copies of ${esc(r.name)} to keep">
      <button type="submit" class="ghost" disabled>Save</button></form></td><td>${fmt(r.spare)}</td></tr>`).join("");
    form.querySelector("[data-empty]").hidden = rows.length > 0;
    form.querySelector("[data-more]").hidden = rows.length <= copyFilters.limit;
  }
  form.querySelector("[type=search]").oninput = e => { copyFilters.query = e.target.value; copyFilters.limit = 80; draw(); };
  form.querySelector("[data-mastered]").onchange = e => { location.hash = "inventory?tab=copies" + (e.target.checked ? "&review=mastered" : ""); };
  form.querySelector("[data-surplus]").onchange = e => { copyFilters.surplus = e.target.checked; copyFilters.limit = 80; draw(); };
  form.querySelector("[data-more]").onclick = () => { copyFilters.limit += 80; draw(); };
  form.addEventListener("input", e => {
    if (e.target.type === "number") e.target.form.querySelector("button").disabled = !e.target.validity.valid || e.target.value === e.target.defaultValue;
  });
  form.addEventListener("submit", async e => {
    e.preventDefault();
    const editor = e.target, input = editor.querySelector("input"), button = editor.querySelector("button");
    if (!input.reportValidity()) return;
    const minimum = input.valueAsNumber;
    input.disabled = button.disabled = true;
    const saved = await act("inventory/keep", { accountKey: data.accountKey, uid: editor.dataset.uid, minimum }, "Minimum saved");
    if (saved) window.dispatchEvent(new CustomEvent("wfp:changed"));
    else { input.disabled = false; button.disabled = false; }
  });
  draw();
}

let renderVersion = 0;
let lastState, lastAccount, refreshInventory;
const signature = state => JSON.stringify([state?.account?.key, state?.sources?.inventory?.synced, state?.sources?.inventory?.imported, state?.sources?.inventory?.pending, state?.tasks?.sig, state?.ready]);

export default {
  id: "inventory",
  title: "Inventory",
  icon: "inventory",
  onState(state) {
    const next = signature(state);
    if (lastAccount === state?.account?.key && document.querySelector("[data-copies] form button:not(:disabled)")) return;
    if (refreshInventory && next !== lastState) refreshInventory();
  },
  async render(view, params, app) {
    const version = ++renderVersion;
    const route = location.hash;
    const tab = tabs.find(([id]) => id === params.get("tab"))?.[0] || "items";
    lastState = signature(app.state);
    lastAccount = app.state?.account?.key;
    refreshInventory = app.refresh;
    const data = await api(tab === "items" ? "inventory" : "inventory/" + tab);
    if (version !== renderVersion || location.hash !== route) return;
    if (tab !== "items") {
      view.innerHTML = `<div class="page"><div class="page-head"><h1>Inventory</h1></div>${navigation(tab)}
        ${tab === "changes" ? changesPanel(data) : '<section data-copies></section>'}</div>`;
      if (tab === "copies") copiesPanel(view, data, params.get("review") === "mastered");
      return;
    }
    const items = data.items || [];
    if (!data.source) {
      view.innerHTML = `<div class="page"><div class="page-head"><div><h1>Inventory</h1></div></div>
        ${navigation(tab)}<div class="empty"><b>No inventory loaded yet</b>${data.pending ? `<p>${fmt(data.pending)} recorded reward pick(s) await the first confirmed inventory.</p>` : ""}Import an inventory JSON file in <a href="#settings">Settings</a>,
        or enable memory reads for automatic updates after mission syncs.</div></div>`;
      return;
    }
    const c = data.currencies || {};
    const cur = (icon, n, label) => (n == null ? "" : `<div class="tile cur"><img src="icons/${icon}.png" alt="" width="26" height="26"><div><b>${fmt(n)}</b><span>${label}</span></div></div>`);
    const kinds = new Map();
    for (const x of items) kinds.set(group(x.kind), (kinds.get(group(x.kind)) || 0) + 1);
    const chips = [...kinds].sort((a, b) => b[1] - a[1]);
    view.innerHTML = `<div class="page">
      <div class="page-head"><div><h1>Inventory</h1><p>${fmt(items.length)} stacks Â· ${esc(data.source || "")} ${ago(data.imported)}</p></div></div>
      ${navigation(tab)}${data.pending ? `<p class="muted">Includes ${fmt(data.pending)} recorded ${data.pending === 1 ? "pick or build" : "picks or builds"} awaiting inventory sync.</p>` : ""}<div class="tiles cur-row">${cur("platinum", c.platinum, "Platinum")}${cur("credits", c.credits, "Credits")}${cur("endo", c.endo, "Endo")}${cur("ducats", c.ducats, "Ducats")}</div>
      <div class="toolbar">
        <input type="search" id="q" placeholder="Search the inventory" value="${esc(st.q)}" aria-label="Search">
        <div class="seg" id="sort">${SORTS.map(([v, l]) => `<button data-v="${v}" class="${st.sort === v ? "on" : ""}">${l}</button>`).join("")}</div>
        <label class="check"><input type="checkbox" id="trade" ${st.tradable ? "checked" : ""}>Tradable</label>
        <span class="count" id="cnt"></span>
      </div>
      <div class="toolbar" aria-label="Inventory use filters">
        <label class="check"><input type="checkbox" id="weapons" ${st.weapons ? "checked" : ""}>Weapons</label>
        <label class="check"><input type="checkbox" id="mastered" ${st.mastered ? "checked" : ""}>Mastered</label>
        <label>Crafting use <select id="crafting">${[["", "Any"], ["used", "Used in a recipe"], ["none", "No known crafting uses"], ["unknown", "Recipe data unavailable"]].map(([v, l]) => `<option value="${v}" ${st.crafting === v ? "selected" : ""}>${l}</option>`).join("")}</select></label>
        <button id="review-weapons" class="ghost" title="Show mastered weapons with no known crafting uses">Review mastered weapons</button>
        <a class="btn ghost" href="#inventory?tab=copies&amp;review=mastered">Copies to keep for mastered weapons</a>
        <button id="clear-filters" class="ghost">Clear filters</button>
      </div>
      <p class="muted">Crafting uses are based on loaded recipes. Mastery is account-wide; check upgrades, loadouts and individual copies before selling in-game.</p>
      <div class="kind-chips" id="kinds"><button class="chip ${st.kind ? "" : "on"}" data-kind="">All <span>${fmt(items.length)}</span></button>
        ${chips.map(([k, n]) => `<button class="chip ${st.kind === k ? "on" : ""}" data-kind="${esc(k)}">${esc(k)} <span>${fmt(n)}</span></button>`).join("")}</div>
      <div class="inv-grid" id="grid"></div>
      <div id="more" style="margin-top:12px"></div></div>`;
    const draw = () => {
      const q = st.q.toLowerCase();
      const rows = items.filter((x) => (!st.kind || group(x.kind) === st.kind) && (!st.tradable || x.slug)
        && (!st.weapons || x.isWeapon) && (!st.mastered || x.mastered === true)
        && (!st.crafting || (st.crafting === "used" ? x.craftingUses > 0 : st.crafting === "none" ? x.craftingKnown && !x.craftingUses : !x.craftingKnown && !x.craftingUses))
        && (!q || x.name.toLowerCase().includes(q) || x.kind.toLowerCase().includes(q)));
      rows.sort(st.sort === "count" ? (a, b) => b.count - a.count
        : st.sort === "value" ? (a, b) => value(b) - value(a) || (b.ducats || 0) - (a.ducats || 0)
          : (a, b) => a.name.localeCompare(b.name));
      $("#cnt").textContent = `${fmt(rows.length)} shown`;
      $("#clear-filters").disabled = !(st.q || st.kind || st.tradable || st.weapons || st.mastered || st.crafting);
      $("#grid").innerHTML = rows.slice(0, st.limit).map(tile).join("")
        || `<div class="empty" style="grid-column:1/-1"><b>Nothing matches</b>Change the search or use Clear filters to show all items.</div>`;
      $("#more").innerHTML = rows.length > st.limit ? `<button id="moreBtn">Show ${fmt(Math.min(240, rows.length - st.limit))} more</button>` : "";
      const mb = $("#moreBtn");
      if (mb) mb.onclick = () => { st.limit += 240; draw(); };
    };
    draw();
    $("#clear-filters").onclick = () => {
      st.q = st.kind = st.crafting = ""; st.tradable = st.weapons = st.mastered = false; st.limit = 240;
      $("#q").value = $("#crafting").value = "";
      for (const key of ["trade", "weapons", "mastered"]) $("#" + key).checked = false;
      for (const chip of view.querySelectorAll("#kinds .chip")) chip.classList.toggle("on", chip.dataset.kind === "");
      draw();
    };
    $("#q").oninput = debounce((e) => { st.q = e.target.value; st.limit = 240; draw(); }, 120);
    $("#trade").onchange = (e) => { st.tradable = e.target.checked; draw(); };
    for (const key of ["weapons", "mastered"]) $("#" + key).onchange = (e) => { st[key] = e.target.checked; st.limit = 240; draw(); };
    $("#crafting").onchange = (e) => { st.crafting = e.target.value; st.limit = 240; draw(); };
    $("#review-weapons").onclick = () => {
      st.weapons = st.mastered = true; st.crafting = "none"; st.kind = ""; st.q = ""; st.tradable = false; st.limit = 240;
      $("#weapons").checked = $("#mastered").checked = true; $("#crafting").value = "none"; $("#q").value = ""; $("#trade").checked = false;
      for (const x of view.querySelectorAll("#kinds .chip")) x.classList.toggle("on", x.dataset.kind === "");
      draw();
    };
    for (const b of view.querySelectorAll("#sort button")) {
      b.onclick = () => { st.sort = b.dataset.v; for (const x of view.querySelectorAll("#sort button")) x.classList.toggle("on", x === b); draw(); };
    }
    $("#kinds").onclick = (e) => {
      const b = e.target.closest("[data-kind]");
      if (!b) return;
      st.kind = b.dataset.kind;
      st.limit = 240;
      for (const x of view.querySelectorAll("#kinds .chip")) x.classList.toggle("on", x === b);
      draw();
    };
    $("#grid").onclick = (e) => { const t = e.target.closest("[data-uid]"); if (t) drawer.open(t.dataset.uid); };
  },
};
