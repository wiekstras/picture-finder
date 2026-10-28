'use strict';

const MAX_BYTES = 10 * 1024 * 1024;
const TYPES = ['image/jpeg', 'image/png'];
const STORE_KEYS = 'pf.keys';
const STORE_REMEMBER = 'pf.rememberKeys';
const STORE_ENABLED = 'pf.enabledProviders';

const $ = (id) => document.getElementById(id);
const form = $('form');
const fileInput = $('file');
const dropzone = $('dropzone');
const preview = $('preview');
const imageUrlInput = $('imageUrl');
const providersEl = $('providers');
const rememberInput = $('rememberKeys');
const checkButton = $('check');
const formError = $('formError');
const resultsEl = $('results');

let selectedFile = null;
let providers = [];
let lastResult = null;
let filter = 'all';
let query = '';

// ---------- storage (every access can throw in private mode) ----------

function load(storage, key, fallback) {
  try { return JSON.parse(storage.getItem(key)) ?? fallback; } catch { return fallback; }
}
function save(storage, key, value) {
  try { storage.setItem(key, JSON.stringify(value)); } catch { /* ignore */ }
}
function remove(storage, key) {
  try { storage.removeItem(key); } catch { /* ignore */ }
}

const remember = () => rememberInput.checked;
// Remembered keys live in localStorage; otherwise only for this tab (sessionStorage).
const keyStorage = () => (remember() ? localStorage : sessionStorage);

function loadKeys() {
  return load(localStorage, STORE_KEYS, null) ?? load(sessionStorage, STORE_KEYS, {});
}

function saveKeys() {
  const keys = {};
  for (const input of providersEl.querySelectorAll('[data-key]')) {
    if (input.value.trim()) keys[input.dataset.key] = input.value.trim();
  }
  remove(localStorage, STORE_KEYS);
  remove(sessionStorage, STORE_KEYS);
  save(keyStorage(), STORE_KEYS, keys);
}

function saveEnabled() {
  save(localStorage, STORE_ENABLED, providers.filter((p) => p.enabled).map((p) => p.id));
}

// ---------- photo input ----------

function setFile(file) {
  formError.hidden = true;
  if (file && !TYPES.includes(file.type)) return showFormError('Only JPG and PNG images are supported.');
  if (file && file.size > MAX_BYTES) return showFormError('Image is larger than 10 MB.');

  selectedFile = file || null;
  if (preview.dataset.objectUrl) URL.revokeObjectURL(preview.dataset.objectUrl);
  delete preview.dataset.objectUrl;

  if (selectedFile) {
    preview.dataset.objectUrl = URL.createObjectURL(selectedFile);
    preview.src = preview.dataset.objectUrl;
    $('fileName').textContent = `${selectedFile.name} · ${(selectedFile.size / 1024 / 1024).toFixed(1)} MB`;
  }
  $('fileMeta').hidden = !selectedFile;
  updatePreview();
  updateProviderHints();
}

function updatePreview() {
  const url = validUrl(imageUrlInput.value);
  if (!selectedFile && url) preview.src = url;
  const show = Boolean(selectedFile || url);
  preview.hidden = !show;
  dropzone.querySelector('.dz-empty').hidden = show;
  dropzone.classList.toggle('has-image', show);
  if (!show) preview.removeAttribute('src');
}

function validUrl(value) {
  try {
    const url = new URL(value.trim());
    return url.protocol === 'http:' || url.protocol === 'https:' ? url.href : null;
  } catch { return null; }
}

fileInput.addEventListener('change', () => setFile(fileInput.files[0]));
$('clearFile').addEventListener('click', () => { fileInput.value = ''; setFile(null); });
dropzone.addEventListener('keydown', (e) => {
  if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); fileInput.click(); }
});
for (const type of ['dragenter', 'dragover']) {
  dropzone.addEventListener(type, (e) => { e.preventDefault(); dropzone.classList.add('dragging'); });
}
for (const type of ['dragleave', 'drop']) {
  dropzone.addEventListener(type, () => dropzone.classList.remove('dragging'));
}
dropzone.addEventListener('drop', (e) => {
  e.preventDefault();
  const file = e.dataTransfer.files[0];
  if (file) setFile(file);
});
document.addEventListener('paste', (e) => {
  const file = [...(e.clipboardData?.files ?? [])].find((f) => f.type.startsWith('image/'));
  if (file) setFile(file);
});

imageUrlInput.addEventListener('input', () => {
  updatePreview();
  updateManualLinks();
  updateProviderHints();
});

// Links to the public reverse image search sites for a quick manual comparison.
function updateManualLinks() {
  const box = $('manualLinks');
  box.querySelectorAll('a').forEach((a) => a.remove());
  const url = validUrl(imageUrlInput.value);
  box.hidden = !url;
  if (!url) return;
  const e = encodeURIComponent(url);
  const sites = [
    ['Google Lens', `https://lens.google.com/uploadbyurl?url=${e}`],
    ['Bing', `https://www.bing.com/images/search?view=detailv2&iss=sbi&q=imgurl:${e}`],
    ['Yandex', `https://yandex.com/images/search?rpt=imageview&url=${e}`],
    ['TinEye', `https://tineye.com/search?url=${e}`],
  ];
  for (const [name, href] of sites) {
    box.append(el('a', { href, target: '_blank', rel: 'noopener noreferrer', textContent: name }));
  }
}

// ---------- providers ----------

async function loadProviders() {
  try {
    const res = await fetch('/api/providers');
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    providers = await res.json();
  } catch (err) {
    providersEl.replaceChildren(el('p', { className: 'form-error', textContent: `Could not load providers: ${err.message}` }));
    return;
  }

  const savedKeys = loadKeys();
  const savedEnabled = load(localStorage, STORE_ENABLED, null);
  rememberInput.checked = load(localStorage, STORE_REMEMBER, false);

  providersEl.replaceChildren();
  for (const p of providers) {
    const hasKey = p.keyFields.some((f) => savedKeys[`${p.id}:${f.name}`]);
    p.enabled = savedEnabled ? savedEnabled.includes(p.id) : p.id === 'google' || p.isConfiguredOnServer || hasKey;
    providersEl.append(renderProvider(p, savedKeys));
  }
  updateProviderHints();
}

function renderProvider(p, savedKeys) {
  const card = el('div', { className: 'provider' });
  card.classList.toggle('enabled', p.enabled);

  const toggle = el('input', { type: 'checkbox', checked: p.enabled });
  toggle.setAttribute('aria-label', `Use ${p.name}`);
  const switchEl = el('label', { className: 'switch' });
  switchEl.append(toggle, el('span'));

  const tags = el('div', { className: 'tags' });
  if (p.supportsUpload) tags.append(el('span', { className: 'tag', textContent: 'upload' }));
  if (p.supportsUrl) tags.append(el('span', { className: 'tag', textContent: 'URL' }));
  if (p.isConfiguredOnServer) tags.append(el('span', { className: 'tag server', textContent: 'key set on server' }));
  const hint = el('span', { className: 'tag', hidden: true });
  tags.append(hint);
  p.hintEl = hint;

  const info = el('div', { className: 'info' });
  info.append(el('strong', { textContent: p.name }), el('p', { textContent: p.description }), tags);

  const head = el('div', { className: 'provider-head' });
  head.append(switchEl, info);

  const keys = el('div', { className: 'keys', hidden: !p.enabled });
  for (const f of p.keyFields) keys.append(renderKeyField(p, f, savedKeys));

  toggle.addEventListener('change', () => {
    p.enabled = toggle.checked;
    card.classList.toggle('enabled', p.enabled);
    keys.hidden = !p.enabled;
    saveEnabled();
  });

  card.append(head, keys);
  return card;
}

function renderKeyField(p, f, savedKeys) {
  const id = `key-${p.id}-${f.name}`;
  const wrap = el('div', { className: 'key-field' });
  wrap.append(el('label', { htmlFor: id, textContent: f.label + (p.isConfiguredOnServer ? ' (optional)' : '') }));

  const value = savedKeys[`${p.id}:${f.name}`] ?? '';
  const row = el('div', { className: 'key-input' });

  if (f.multiline) {
    const textarea = el('textarea', { id, value, placeholder: '{ "type": "service_account", ... }', spellcheck: false });
    textarea.dataset.key = `${p.id}:${f.name}`;
    textarea.addEventListener('input', saveKeys);

    const fileButton = el('button', { type: 'button', className: 'icon-btn', textContent: 'Load file' });
    const jsonFile = el('input', { type: 'file', accept: 'application/json,.json', hidden: true });
    fileButton.addEventListener('click', () => jsonFile.click());
    jsonFile.addEventListener('change', async () => {
      if (!jsonFile.files[0]) return;
      textarea.value = await jsonFile.files[0].text();
      jsonFile.value = '';
      saveKeys();
    });
    row.append(textarea, fileButton, jsonFile);
  } else {
    const input = el('input', { id, type: 'password', value, autocomplete: 'off', spellcheck: false });
    input.dataset.key = `${p.id}:${f.name}`;
    input.addEventListener('input', saveKeys);

    const show = el('button', { type: 'button', className: 'icon-btn', textContent: 'Show' });
    show.addEventListener('click', () => {
      const hidden = input.type === 'password';
      input.type = hidden ? 'text' : 'password';
      show.textContent = hidden ? 'Hide' : 'Show';
    });
    row.append(input, show);
  }

  wrap.append(row);
  if (f.help) wrap.append(el('small', { textContent: f.help }));
  return wrap;
}

// Tell the user up front when a provider can't run with the current input.
function updateProviderHints() {
  const hasUrl = Boolean(validUrl(imageUrlInput.value));
  for (const p of providers) {
    if (!p.hintEl) continue;
    const needsUrl = !p.supportsUpload && !hasUrl;
    p.hintEl.hidden = !needsUrl;
    p.hintEl.textContent = needsUrl ? 'needs image URL' : '';
  }
}

rememberInput.addEventListener('change', () => {
  save(localStorage, STORE_REMEMBER, remember());
  saveKeys();
});

// ---------- check ----------

form.addEventListener('submit', async (e) => {
  e.preventDefault();
  formError.hidden = true;

  const imageUrl = imageUrlInput.value.trim();
  if (!selectedFile && !imageUrl) return showFormError('Choose a photo or enter an image URL.');
  if (imageUrl && !validUrl(imageUrl)) return showFormError('The image URL must start with http:// or https://.');

  const enabled = providers.filter((p) => p.enabled);
  if (!enabled.length) return showFormError('Turn on at least one search provider.');

  const body = new FormData();
  if (selectedFile) body.append('image', selectedFile);
  if (imageUrl) body.append('imageUrl', imageUrl);
  body.append('providers', enabled.map((p) => p.id).join(','));
  for (const input of providersEl.querySelectorAll('[data-key]')) {
    const [providerId] = input.dataset.key.split(':');
    if (input.value.trim() && enabled.some((p) => p.id === providerId)) {
      body.append(`key:${input.dataset.key}`, input.value.trim());
    }
  }

  setLoading(true, enabled);
  try {
    const res = await fetch('/api/check', { method: 'POST', body });
    const data = await res.json().catch(() => ({ error: `The server returned ${res.status}.` }));
    if (!res.ok) return renderError(data.error || `The server returned ${res.status}.`);
    lastResult = data;
    filter = 'all';
    query = '';
    renderResults();
  } catch (err) {
    renderError(`Could not reach the server: ${err.message}`);
  } finally {
    setLoading(false);
  }
});

function setLoading(loading, enabled = []) {
  checkButton.disabled = loading;
  checkButton.querySelector('.spinner').hidden = !loading;
  checkButton.querySelector('.btn-label').textContent = loading ? 'Searching…' : 'Check';
  if (!loading) return;

  const status = el('div', { className: 'provider-status' });
  for (const p of enabled) status.append(el('span', { className: 'pstatus', textContent: `${p.name}…` }));
  const list = el('div', { className: 'result-list' });
  for (let i = 0; i < 4; i++) list.append(el('div', { className: 'skeleton' }));
  resultsEl.replaceChildren(status, list);
}

function showFormError(message) {
  formError.textContent = message;
  formError.hidden = false;
}

function renderError(message) {
  const box = el('div', { className: 'empty error-state' });
  box.append(el('h2', { textContent: 'Something went wrong' }), el('p', { textContent: message }));
  resultsEl.replaceChildren(box);
}

// ---------- results ----------

function renderResults() {
  const data = lastResult;
  const pages = data.pagesWithMatchingImages;
  const counts = {
    all: pages.length,
    nl: pages.filter((p) => p.isDutch).length,
    full: pages.filter((p) => p.matchType === 'full').length,
    partial: pages.filter((p) => p.matchType === 'partial').length,
  };

  const stats = el('div', { className: 'stats' });
  for (const [cls, n, label] of [['', counts.all, 'Pages found'], ['nl', counts.nl, 'Dutch (.nl) pages'], ['full', counts.full, 'Full matches'], ['partial', counts.partial, 'Partial matches']]) {
    const stat = el('div', { className: `stat ${cls}` });
    stat.append(el('b', { textContent: n }), el('span', { textContent: label }));
    stats.append(stat);
  }

  const status = el('div', { className: 'provider-status' });
  for (const p of data.providers) {
    const chip = el('span', { className: `pstatus ${p.status}` });
    chip.append(p.name);
    const detail = p.status === 'ok'
      ? `${p.resultCount} pages · ${(p.durationMs / 1000).toFixed(1)}s`
      : p.message;
    chip.append(el('small', { textContent: ` · ${detail}` }));
    chip.title = p.message || '';
    status.append(chip);
  }

  const children = [stats, status];

  if (data.bestGuessLabels.length) {
    const labels = el('div', { className: 'labels', textContent: 'Best guess:' });
    for (const label of data.bestGuessLabels) labels.append(el('span', { textContent: label }));
    children.push(labels);
  }

  const toolbar = el('div', { className: 'toolbar' });
  const segments = el('div', { className: 'segments', role: 'tablist' });
  for (const [key, label] of [['all', 'All'], ['nl', 'NL'], ['full', 'Full'], ['partial', 'Partial']]) {
    const button = el('button', { type: 'button', className: filter === key ? 'active' : '', textContent: label });
    button.append(el('span', { textContent: counts[key] }));
    button.addEventListener('click', () => { filter = key; renderList(); updateSegments(); });
    button.dataset.filter = key;
    segments.append(button);
  }
  const search = el('input', { type: 'text', placeholder: 'Filter by title or domain…', value: query });
  search.addEventListener('input', () => { query = search.value.trim().toLowerCase(); renderList(); });
  const csv = el('button', { type: 'button', className: 'secondary', textContent: 'Export CSV' });
  csv.addEventListener('click', exportCsv);
  toolbar.append(segments, search, csv);

  const list = el('ul', { className: 'result-list', id: 'resultList' });
  children.push(toolbar, list);

  children.push(...imageSection('Full matching images', 'exact copies, no page found', data.fullMatchingImages));
  children.push(...imageSection('Partial matching images', 'likely crops or edits', data.partialMatchingImages));

  resultsEl.replaceChildren(...children);
  renderList();

  function updateSegments() {
    for (const b of segments.querySelectorAll('button')) b.classList.toggle('active', b.dataset.filter === filter);
  }
}

function visiblePages() {
  return lastResult.pagesWithMatchingImages.filter((p) =>
    (filter === 'all' || (filter === 'nl' ? p.isDutch : p.matchType === filter)) &&
    (!query || p.pageTitle.toLowerCase().includes(query) || p.url.toLowerCase().includes(query)));
}

function renderList() {
  const list = $('resultList');
  const pages = visiblePages();
  if (!pages.length) {
    list.replaceChildren(el('li', {
      className: 'no-results',
      textContent: lastResult.pagesWithMatchingImages.length ? 'No pages match this filter.' : 'No pages found that use this photo.',
    }));
    return;
  }

  const names = Object.fromEntries(lastResult.providers.map((p) => [p.id, p.name]));
  const template = $('resultTemplate');
  list.replaceChildren(...pages.map((p) => {
    const li = template.content.firstElementChild.cloneNode(true);
    li.classList.toggle('dutch', p.isDutch);

    const images = [...p.fullMatchingImageUrls, ...p.partialMatchingImageUrls];
    const thumbLink = li.querySelector('.thumb');
    thumbLink.href = images[0] || p.url;
    if (images[0]) setImage(thumbLink.querySelector('img'), images[0]);

    const badges = li.querySelector('.badges');
    if (p.isDutch) badges.append(el('span', { className: 'badge nl', textContent: 'NL' }));
    badges.append(el('span', { className: `badge ${p.matchType}`, textContent: `${p.matchType} match` }));
    for (const id of p.providers) badges.append(el('span', { className: 'badge source', textContent: names[id] || id }));

    const title = li.querySelector('.title');
    title.href = p.url;
    title.textContent = p.pageTitle || p.domain || p.url;
    li.querySelector('.url').textContent = p.url;

    if (images.length) {
      const details = li.querySelector('.images');
      details.hidden = false;
      details.querySelector('summary').textContent = `${images.length} matched image${images.length === 1 ? '' : 's'} on this page`;
      details.querySelector('ul').append(...images.map((url) => {
        const item = el('li');
        item.append(el('a', { href: url, target: '_blank', rel: 'noopener noreferrer', textContent: url }));
        return item;
      }));
    }

    const copy = li.querySelector('.copy');
    copy.addEventListener('click', async () => {
      try {
        await navigator.clipboard.writeText(p.url);
        copy.classList.add('copied');
        setTimeout(() => copy.classList.remove('copied'), 1200);
      } catch { /* clipboard not available */ }
    });
    return li;
  }));
}

function imageSection(title, subtitle, urls) {
  if (!urls.length) return [];
  const heading = el('h3', { className: 'section-title', textContent: `${title} (${urls.length}) ` });
  heading.append(el('small', { textContent: subtitle }));
  const grid = el('div', { className: 'grid' });
  for (const url of urls) {
    const a = el('a', { href: url, target: '_blank', rel: 'noopener noreferrer', title: url });
    const img = el('img', { alt: '', loading: 'lazy', referrerPolicy: 'no-referrer' });
    setImage(img, url);
    a.append(img);
    grid.append(a);
  }
  return [heading, grid];
}

// Many sites block hotlinking; fall back to an empty box.
function setImage(img, url) {
  img.addEventListener('error', () => img.removeAttribute('src'), { once: true });
  img.src = url;
}

function exportCsv() {
  const rows = [['domain', 'url', 'title', 'match', 'dutch', 'providers', 'image_urls']];
  for (const p of visiblePages()) {
    rows.push([p.domain, p.url, p.pageTitle, p.matchType, p.isDutch ? 'yes' : 'no', p.providers.join(' '),
      [...p.fullMatchingImageUrls, ...p.partialMatchingImageUrls].join(' ')]);
  }
  // Prefix values that spreadsheet apps would run as formulas (titles come from other sites).
  const cell = (v) => `"${String(v).replace(/^[=+\-@]/, "'$&").replaceAll('"', '""')}"`;
  const csv = rows.map((r) => r.map(cell).join(',')).join('\r\n');
  const a = el('a', {
    href: URL.createObjectURL(new Blob(['﻿' + csv], { type: 'text/csv' })),
    download: `picture-finder-${new Date().toISOString().slice(0, 10)}.csv`,
  });
  a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 1000);
}

function el(tag, props = {}) {
  return Object.assign(document.createElement(tag), props);
}

loadProviders();
