// Страница документа MD Viewer: редактор (CodeMirror), просмотр, оглавление, поиск.
// Связь с приложением — через window.chrome.webview (сообщения JSON).
import { EditorState, Compartment, EditorSelection } from '@codemirror/state';
import {
  EditorView, keymap, lineNumbers, highlightActiveLine, highlightActiveLineGutter,
  drawSelection, dropCursor, highlightSpecialChars, rectangularSelection,
} from '@codemirror/view';
import { defaultKeymap, history, historyKeymap, indentWithTab, undo as cmUndo, redo as cmRedo } from '@codemirror/commands';
import { markdown, markdownLanguage } from '@codemirror/lang-markdown';
import { syntaxHighlighting, defaultHighlightStyle, HighlightStyle, bracketMatching } from '@codemirror/language';
import { search, searchKeymap, openSearchPanel, closeSearchPanel, highlightSelectionMatches } from '@codemirror/search';
import { oneDark } from '@codemirror/theme-one-dark';
import { tags } from '@lezer/highlight';
import hljs from 'highlight.js/lib/common';
import katex from 'katex';

const SCRIPT_URL = (document.currentScript && document.currentScript.src) || 'https://app.mdviewer.example/lib/app.bundle.js';
const APP_ORIGIN = new URL(SCRIPT_URL).origin;
const DOC_ORIGIN = 'https://doc.mdviewer.example';

const webview = window.chrome && window.chrome.webview;
const outbox = [];
function post(msg) {
  if (webview) webview.postMessage(msg);
  else outbox.push(msg);
}

const S = {
  mode: 'view',
  theme: 'light',
  effectiveTheme: 'light',
  version: 0,
  renderedVersion: -1,
  toc: [],
  silent: false,
  exporting: false,
  syncLock: 0,
  zoom: 1,
  tocVisible: true,
  mermaidCache: new Map(),
  mermaidSeq: 0,
  renderToken: 0,
  lineEls: [],
  pendingTimer: 0,
  find: { open: false, ranges: [], index: -1 },
  lastSentText: null,
};

const $ = (id) => document.getElementById(id);
let root, previewPane, preview, tocEl, tocList, editorHost, view;

// ---------- Редактор ----------

const ruPhrases = {
  'Find': 'Найти',
  'Replace': 'Заменить',
  'next': 'далее',
  'previous': 'назад',
  'all': 'все',
  'match case': 'учитывать регистр',
  'regexp': 'рег. выражение',
  'by word': 'слово целиком',
  'replace': 'заменить',
  'replace all': 'заменить все',
  'close': 'закрыть',
  'current match': 'текущее совпадение',
  'replaced $ matches': 'заменено совпадений: $',
  'replaced match on line $': 'заменено в строке $',
  'on line': 'в строке',
  'Go to line': 'Перейти к строке',
  'go': 'перейти',
};

const lightMd = HighlightStyle.define([
  { tag: tags.heading1, fontWeight: 'bold', fontSize: '1.3em' },
  { tag: tags.heading2, fontWeight: 'bold', fontSize: '1.2em' },
  { tag: tags.heading3, fontWeight: 'bold', fontSize: '1.1em' },
  { tag: [tags.heading4, tags.heading5, tags.heading6], fontWeight: 'bold' },
  { tag: tags.strong, fontWeight: 'bold' },
  { tag: tags.emphasis, fontStyle: 'italic' },
  { tag: tags.strikethrough, textDecoration: 'line-through' },
  { tag: tags.link, color: '#0969da' },
  { tag: tags.url, color: '#0550ae' },
  { tag: tags.monospace, color: '#953800' },
  { tag: tags.quote, color: '#57606a', fontStyle: 'italic' },
  { tag: tags.processingInstruction, color: '#8250df' },
]);

const themeComp = new Compartment();
const wrapComp = new Compartment();
const fontComp = new Compartment();

function editorTheme(dark) {
  return dark ? [oneDark] : [syntaxHighlighting(lightMd), EditorView.theme({}, { dark: false })];
}

function fontTheme(family, size) {
  return EditorView.theme({
    '&': { fontSize: (size || 14) + 'px' },
    '.cm-content, .cm-gutters': { fontFamily: `${family ? `"${family}", ` : ''}Consolas, "Cascadia Mono", monospace` },
  });
}

function wrapSel(view, before, after, placeholder) {
  const { state } = view;
  const tr = state.changeByRange((range) => {
    const text = state.sliceDoc(range.from, range.to);
    // Снять разметку, если она уже есть вокруг выделения
    const pre = state.sliceDoc(range.from - before.length, range.from);
    const post2 = state.sliceDoc(range.to, range.to + after.length);
    if (!range.empty && pre === before && post2 === after) {
      return {
        changes: [{ from: range.from - before.length, to: range.from }, { from: range.to, to: range.to + after.length }],
        range: EditorSelection.range(range.from - before.length, range.to - before.length),
      };
    }
    const inner = text || placeholder;
    return {
      changes: { from: range.from, to: range.to, insert: before + inner + after },
      range: EditorSelection.range(range.from + before.length, range.from + before.length + inner.length),
    };
  });
  view.dispatch(state.update(tr, { scrollIntoView: true, userEvent: 'input' }));
  view.focus();
  return true;
}

function selectedLines(state) {
  const lines = new Set();
  for (const r of state.selection.ranges) {
    const a = state.doc.lineAt(r.from).number;
    const b = state.doc.lineAt(r.to).number;
    for (let i = a; i <= b; i++) lines.add(i);
  }
  return [...lines].sort((x, y) => x - y).map((n) => state.doc.line(n));
}

function togglePrefix(view, prefix, re) {
  const { state } = view;
  const lines = selectedLines(state);
  const all = lines.every((l) => re.test(l.text));
  const changes = [];
  for (const l of lines) {
    if (all) {
      const m = l.text.match(re);
      changes.push({ from: l.from, to: l.from + m[0].length, insert: '' });
    } else if (!re.test(l.text)) {
      // Убрать маркер другого списка, чтобы не получилось «- 1. текст»
      const other = l.text.match(/^(\s*)([-*+] \[[ xX]\] |[-*+] |\d+[.)] |> )/);
      if (other) changes.push({ from: l.from + other[1].length, to: l.from + other[0].length, insert: prefix });
      else {
        const indent = l.text.match(/^\s*/)[0].length;
        changes.push({ from: l.from + indent, insert: prefix });
      }
    }
  }
  view.dispatch({ changes, userEvent: 'input', scrollIntoView: true });
  view.focus();
  return true;
}

function cycleHeading(view) {
  const { state } = view;
  const changes = [];
  for (const l of selectedLines(state)) {
    const m = l.text.match(/^(#{1,6}) /);
    if (!m) changes.push({ from: l.from, insert: '# ' });
    else if (m[1].length >= 6) changes.push({ from: l.from, to: l.from + m[0].length, insert: '' });
    else changes.push({ from: l.from, insert: '#' });
  }
  view.dispatch({ changes, userEvent: 'input', scrollIntoView: true });
  view.focus();
  return true;
}

function insertLink(view) {
  const { state } = view;
  const tr = state.changeByRange((range) => {
    const text = state.sliceDoc(range.from, range.to) || 'текст ссылки';
    const url = 'https://';
    const insert = `[${text}](${url})`;
    const urlFrom = range.from + text.length + 3;
    return { changes: { from: range.from, to: range.to, insert }, range: EditorSelection.range(urlFrom, urlFrom + url.length) };
  });
  view.dispatch(state.update(tr, { scrollIntoView: true, userEvent: 'input' }));
  view.focus();
  return true;
}

function insertCode(view) {
  const { state } = view;
  const r = state.selection.main;
  const text = state.sliceDoc(r.from, r.to);
  if (text.includes('\n')) {
    const insert = '```\n' + text + (text.endsWith('\n') ? '' : '\n') + '```';
    view.dispatch({ changes: { from: r.from, to: r.to, insert }, userEvent: 'input', scrollIntoView: true });
    view.focus();
    return true;
  }
  return wrapSel(view, '`', '`', 'код');
}

function insertBlock(view, block) {
  const { state } = view;
  const r = state.selection.main;
  const line = state.doc.lineAt(r.from);
  const needNl = line.text.length > 0 ? '\n\n' : '';
  const insert = needNl + block + '\n';
  const at = line.text.length > 0 ? line.to : r.from;
  view.dispatch({ changes: { from: at, insert }, selection: { anchor: at + insert.length }, userEvent: 'input', scrollIntoView: true });
  view.focus();
  return true;
}

function insertTable(view) {
  return insertBlock(view, '| Заголовок | Заголовок |\n| --- | --- |\n| Ячейка | Ячейка |');
}

function insertText(text) {
  if (!view) return;
  const r = view.state.selection.main;
  view.dispatch({ changes: { from: r.from, to: r.to, insert: text }, selection: { anchor: r.from + text.length }, userEvent: 'input', scrollIntoView: true });
  view.focus();
}

const formatCommands = {
  bold: () => wrapSel(view, '**', '**', 'жирный текст'),
  italic: () => wrapSel(view, '*', '*', 'курсив'),
  heading: () => cycleHeading(view),
  list: () => togglePrefix(view, '- ', /^\s*[-*+] (?!\[[ xX]\] )/),
  task: () => togglePrefix(view, '- [ ] ', /^\s*[-*+] \[[ xX]\] /),
  quote: () => togglePrefix(view, '> ', /^\s*> ?/),
  code: () => insertCode(view),
  link: () => insertLink(view),
  table: () => insertTable(view),
  image: () => { post({ type: 'pickImage' }); return true; },
};

function createEditor() {
  const state = EditorState.create({
    doc: '',
    extensions: [
      EditorState.phrases.of(ruPhrases),
      lineNumbers(),
      highlightActiveLineGutter(),
      highlightSpecialChars(),
      history(),
      drawSelection(),
      dropCursor(),
      rectangularSelection(),
      highlightActiveLine(),
      bracketMatching(),
      highlightSelectionMatches(),
      search({ top: true }),
      markdown({ base: markdownLanguage, addKeymap: true }),
      syntaxHighlighting(defaultHighlightStyle, { fallback: true }),
      themeComp.of(editorTheme(false)),
      wrapComp.of(EditorView.lineWrapping),
      fontComp.of(fontTheme('Consolas', 14)),
      keymap.of([
        { key: 'Mod-b', run: formatCommands.bold },
        { key: 'Mod-i', run: formatCommands.italic },
        { key: 'Mod-k', run: formatCommands.link },
        { key: 'Mod-h', run: (v) => { openReplace(v); return true; } },
        ...searchKeymap,
        ...historyKeymap,
        ...defaultKeymap,
        indentWithTab,
      ]),
      EditorView.updateListener.of((u) => {
        if (u.docChanged && !S.silent) scheduleTextChanged();
        if (u.geometryChanged || u.viewportChanged) scheduleSync();
      }),
      EditorView.domEventHandlers({
        paste: (e) => handlePaste(e),
        scroll: () => { scheduleSync(); scheduleTocHighlight(); },
      }),
    ],
  });
  view = new EditorView({ state, parent: editorHost });
  view.scrollDOM.addEventListener('scroll', () => { scheduleSync(); scheduleTocHighlight(); }, { passive: true });
}

function openReplace(v) {
  openSearchPanel(v);
  setTimeout(() => {
    const f = v.dom.querySelector('.cm-search input[name=replace]');
    if (f) f.focus();
  }, 0);
}

function scheduleTextChanged() {
  clearTimeout(S.pendingTimer);
  S.pendingTimer = setTimeout(sendText, 120);
}

function sendText() {
  S.pendingTimer = 0;
  const text = view.state.doc.toString();
  S.version++;
  S.lastSentText = text;
  post({ type: 'textChanged', text, version: S.version });
}

function setText(text, keepScroll) {
  const top = view.scrollDOM.scrollTop;
  S.silent = true;
  try {
    view.dispatch({
      changes: { from: 0, to: view.state.doc.length, insert: text },
      selection: { anchor: 0 },
    });
  } finally {
    S.silent = false;
  }
  if (keepScroll) view.scrollDOM.scrollTop = top;
  else view.scrollDOM.scrollTop = 0;
}

function handlePaste(e) {
  const items = [...((e.clipboardData && e.clipboardData.items) || [])];
  const img = items.find((i) => i.kind === 'file' && i.type.startsWith('image/'));
  if (!img) return false;
  const hasText = items.some((i) => i.kind === 'string' && i.type === 'text/plain');
  if (hasText) return false;
  e.preventDefault();
  const file = img.getAsFile();
  if (!file) return true;
  const reader = new FileReader();
  reader.onload = () => {
    const data = String(reader.result || '');
    post({ type: 'pasteImage', mime: file.type, data: data.substring(data.indexOf(',') + 1) });
  };
  reader.readAsDataURL(file);
  return true;
}

// ---------- Просмотр ----------

function errorBox(text) {
  const d = document.createElement('div');
  d.className = 'mdv-error';
  d.textContent = text;
  return d;
}

function highlightCode(rootEl) {
  for (const code of rootEl.querySelectorAll('pre > code')) {
    const pre = code.parentElement;
    if (pre.parentElement && pre.parentElement.classList.contains('code-wrap')) continue;
    const m = /(?:^|\s)language-([\w#+.-]+)/.exec(code.className);
    const lang = m && m[1].toLowerCase();
    try {
      if (lang && hljs.getLanguage(lang)) {
        code.innerHTML = hljs.highlight(code.textContent, { language: lang, ignoreIllegals: true }).value;
        code.classList.add('hljs');
      }
    } catch (_) { /* оставить без подсветки */ }
    const wrap = document.createElement('div');
    wrap.className = 'code-wrap';
    pre.replaceWith(wrap);
    wrap.appendChild(pre);
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'code-copy';
    btn.textContent = 'Копировать';
    btn.title = 'Копировать код';
    wrap.appendChild(btn);
  }
}

function copyCode(btn) {
  const code = btn.parentElement.querySelector('code');
  const text = code ? code.textContent : '';
  const done = () => {
    btn.textContent = 'Скопировано';
    setTimeout(() => { btn.textContent = 'Копировать'; }, 1500);
  };
  post({ type: 'copy', text });
  done();
}

function renderMath(rootEl) {
  for (const el of rootEl.querySelectorAll('.math')) {
    if (el.dataset.done) continue;
    let tex = el.textContent.trim();
    const display = el.tagName === 'DIV';
    if (tex.startsWith('\\(') && tex.endsWith('\\)')) tex = tex.slice(2, -2);
    else if (tex.startsWith('\\[') && tex.endsWith('\\]')) tex = tex.slice(2, -2);
    else if (tex.startsWith('$$') && tex.endsWith('$$') && tex.length >= 4) tex = tex.slice(2, -2);
    el.dataset.done = '1';
    try {
      katex.render(tex.trim(), el, { displayMode: display, throwOnError: true, output: 'htmlAndMathml', strict: 'ignore' });
      el.classList.add('math-ok');
    } catch (e) {
      el.textContent = '';
      el.classList.add('math-error');
      el.appendChild(errorBox('Ошибка в формуле: ' + ((e && e.message) || e)));
    }
  }
}

let mermaidLoading = null;
function loadMermaid() {
  if (window.mermaid) return Promise.resolve(window.mermaid);
  if (!mermaidLoading) {
    mermaidLoading = new Promise((resolve, reject) => {
      const s = document.createElement('script');
      s.src = APP_ORIGIN + '/lib/mermaid/mermaid.min.js';
      s.onload = () => (window.mermaid ? resolve(window.mermaid) : reject(new Error('Mermaid не загрузился')));
      s.onerror = () => { mermaidLoading = null; reject(new Error('не удалось загрузить Mermaid')); };
      document.head.appendChild(s);
    });
  }
  return mermaidLoading;
}

function mermaidTheme() {
  return S.exporting ? 'default' : (S.effectiveTheme === 'dark' ? 'dark' : 'default');
}

async function renderMermaid(rootEl, token) {
  const blocks = [...rootEl.querySelectorAll('div.mermaid:not([data-done])')];
  if (!blocks.length) return;
  let m;
  try {
    m = await loadMermaid();
  } catch (e) {
    for (const b of blocks) { b.textContent = ''; b.appendChild(errorBox('Ошибка в схеме: ' + e.message)); b.dataset.done = '1'; }
    return;
  }
  const theme = mermaidTheme();
  m.initialize({ startOnLoad: false, securityLevel: 'strict', theme, fontFamily: 'Segoe UI, sans-serif' });
  for (const b of blocks) {
    if (token !== S.renderToken && !S.exporting) return;
    if (!b.isConnected || b.dataset.done) continue;
    const src = b.dataset.src != null ? b.dataset.src : b.textContent;
    b.dataset.src = src;
    b.dataset.done = '1';
    const key = theme + '\n' + src;
    let svg = S.mermaidCache.get(key);
    if (!svg) {
      const id = 'mdv-mermaid-' + (++S.mermaidSeq);
      try {
        const r = await m.render(id, src.trim());
        svg = r.svg;
        if (S.mermaidCache.size > 200) S.mermaidCache.clear();
        S.mermaidCache.set(key, svg);
      } catch (e) {
        for (const junk of [document.getElementById(id), document.getElementById('d' + id)]) {
          if (junk && !preview.contains(junk)) junk.remove();
        }
        b.textContent = '';
        b.classList.add('mermaid-error');
        b.appendChild(errorBox('Ошибка в схеме: ' + String((e && (e.message || e.str)) || e).split('\n').slice(0, 6).join('\n')));
        continue;
      }
    }
    b.innerHTML = svg;
    b.classList.add('mermaid-ok');
  }
}

function resetMermaid() {
  for (const b of preview.querySelectorAll('div.mermaid[data-done]')) {
    delete b.dataset.done;
    b.classList.remove('mermaid-ok', 'mermaid-error');
    b.textContent = b.dataset.src || '';
  }
}

function render(html, toc, version) {
  if (version != null && version < S.renderedVersion) return;
  if (version != null) S.renderedVersion = version;
  const token = ++S.renderToken;
  preview.innerHTML = html;
  highlightCode(preview);
  renderMath(preview);
  S.lineEls = [...preview.querySelectorAll('[data-line]')]
    .map((el) => ({ el, line: Number(el.getAttribute('data-line')) }))
    .filter((x, i, a) => i === 0 || x.line >= a[i - 1].line);
  setToc(toc || []);
  S.mermaidDone = renderMermaid(preview, token).catch(() => {});
  if (S.find.open) runFind(false);
  scheduleTocHighlight();
}

// ---------- Оглавление ----------

function setToc(toc) {
  S.toc = toc;
  tocList.textContent = '';
  if (!toc.length) {
    const li = document.createElement('li');
    li.className = 'toc-empty';
    li.textContent = 'Заголовков нет';
    tocList.appendChild(li);
    return;
  }
  const minLevel = Math.min(...toc.map((t) => t.level));
  toc.forEach((t, i) => {
    const li = document.createElement('li');
    li.className = 'toc-item lvl' + t.level;
    li.style.paddingLeft = (8 + (t.level - minLevel) * 14) + 'px';
    li.textContent = t.text || '(без названия)';
    li.title = t.text;
    li.dataset.index = String(i);
    tocList.appendChild(li);
  });
}

function scrollToHeading(index) {
  const t = S.toc[index];
  if (!t) return;
  S.syncLock = Date.now() + 400;
  if (S.mode !== 'edit') {
    const el = t.id ? document.getElementById(t.id) : null;
    if (el) previewPane.scrollTop = offsetIn(el) - 8;
  }
  if (S.mode !== 'view' && view) {
    const lineNo = Math.min(Math.max(1, t.line + 1), view.state.doc.lines);
    const pos = view.state.doc.line(lineNo).from;
    view.dispatch({ selection: { anchor: pos }, effects: EditorView.scrollIntoView(pos, { y: 'start' }) });
  }
  markToc(index);
}

function markToc(index) {
  for (const li of tocList.querySelectorAll('.toc-item.current')) li.classList.remove('current');
  const li = tocList.querySelector(`.toc-item[data-index="${index}"]`);
  if (li) {
    li.classList.add('current');
    const r = li.getBoundingClientRect();
    const pr = tocEl.getBoundingClientRect();
    if (r.top < pr.top + 30 || r.bottom > pr.bottom) li.scrollIntoView({ block: 'nearest' });
  }
}

let tocTimer = 0;
function scheduleTocHighlight() {
  if (tocTimer) return;
  tocTimer = requestAnimationFrame(() => { tocTimer = 0; updateTocHighlight(); });
}

function updateTocHighlight() {
  if (!S.toc.length) return;
  let current = 0;
  if (S.mode === 'edit') {
    const block = view.lineBlockAtHeight(view.scrollDOM.scrollTop + 4);
    const line = view.state.doc.lineAt(block.from).number - 1;
    S.toc.forEach((t, i) => { if (t.line <= line) current = i; });
  } else {
    const top = previewPane.scrollTop + 12;
    S.toc.forEach((t, i) => {
      const el = t.id ? document.getElementById(t.id) : null;
      if (el && offsetIn(el) <= top) current = i;
    });
  }
  markToc(current);
}

// ---------- Синхронная прокрутка ----------

function offsetIn(el) {
  return el.getBoundingClientRect().top - previewPane.getBoundingClientRect().top + previewPane.scrollTop;
}

let syncTimer = 0;
function scheduleSync() {
  if (S.mode !== 'split' || syncTimer) return;
  syncTimer = requestAnimationFrame(() => { syncTimer = 0; syncPreviewToEditor(); });
}

function syncPreviewToEditor() {
  if (S.mode !== 'split' || Date.now() < S.syncLock || !S.lineEls.length) return;
  const sd = view.scrollDOM;
  if (sd.scrollTop <= 0) { previewPane.scrollTop = 0; return; }
  if (sd.scrollTop + sd.clientHeight >= sd.scrollHeight - 2) { previewPane.scrollTop = previewPane.scrollHeight; return; }
  const block = view.lineBlockAtHeight(sd.scrollTop);
  const line = view.state.doc.lineAt(block.from).number - 1;
  const frac = Math.min(1, Math.max(0, (sd.scrollTop - block.top) / Math.max(1, block.height)));
  const pos = line + frac;
  let a = null, b = null;
  for (const x of S.lineEls) {
    if (x.line <= pos) a = x;
    else { b = x; break; }
  }
  let target;
  if (!a) target = b ? offsetIn(b.el) * (pos / Math.max(1, b.line)) : 0;
  else {
    const pa = offsetIn(a.el);
    const pb = b ? offsetIn(b.el) : previewPane.scrollHeight;
    const la = a.line;
    const lb = b ? b.line : view.state.doc.lines;
    target = pa + (pb - pa) * ((pos - la) / Math.max(1, lb - la));
  }
  previewPane.scrollTop = Math.max(0, target - 4);
}

function previewTopLine() {
  const top = previewPane.scrollTop + 2;
  let line = 0;
  for (const x of S.lineEls) {
    if (offsetIn(x.el) <= top) line = x.line;
    else break;
  }
  return line;
}

// ---------- Ссылки ----------

function slug(s) {
  return s.trim().toLowerCase().replace(/[^\p{L}\p{N}\s_-]/gu, '').replace(/\s/g, '-');
}

function scrollToAnchor(id) {
  let el = document.getElementById(id);
  if (!el) {
    const s = slug(id);
    el = document.getElementById(s) || [...preview.querySelectorAll('[id]')].find((x) => slug(x.id) === s);
  }
  if (!el) return;
  S.syncLock = Date.now() + 400;
  if (S.mode === 'edit') setMode('split');
  previewPane.scrollTop = offsetIn(el) - 8;
  if (S.mode === 'split') {
    const line = el.closest('[data-line]');
    if (line && view) {
      const n = Math.min(view.state.doc.lines, Number(line.getAttribute('data-line')) + 1);
      view.dispatch({ effects: EditorView.scrollIntoView(view.state.doc.line(n).from, { y: 'start' }) });
    }
  }
}

function handleLink(a) {
  const raw = (a.getAttribute('href') || '').trim();
  if (!raw) return;
  if (raw.startsWith('#')) { scrollToAnchor(safeDecode(raw.slice(1))); return; }
  let url;
  try { url = new URL(raw, document.baseURI); } catch (_) { return; }
  if (url.origin === DOC_ORIGIN) {
    post({ type: 'openLocal', path: safeDecode(url.pathname.replace(/^\//, '')), hash: safeDecode(url.hash.replace(/^#/, '')) });
  } else if (url.protocol === 'http:' || url.protocol === 'https:' || url.protocol === 'mailto:') {
    post({ type: 'openExternal', url: url.href });
  }
}

function safeDecode(s) {
  try { return decodeURIComponent(s); } catch (_) { return s; }
}

// ---------- Поиск в просмотре ----------

function openFind() {
  const bar = $('findbar');
  bar.hidden = false;
  S.find.open = true;
  const input = $('find-input');
  const sel = window.getSelection && String(window.getSelection() || '').trim();
  if (sel && sel.length < 200 && !sel.includes('\n')) input.value = sel;
  input.focus();
  input.select();
  runFind(true);
}

function closeFind() {
  $('findbar').hidden = true;
  S.find.open = false;
  S.find.ranges = [];
  if (window.CSS && CSS.highlights) { CSS.highlights.delete('mdv-find'); CSS.highlights.delete('mdv-find-current'); }
  $('find-count').textContent = '';
}

function runFind(jump) {
  const q = $('find-input').value;
  const ranges = [];
  if (q) {
    const ql = q.toLocaleLowerCase();
    const walker = document.createTreeWalker(preview, NodeFilter.SHOW_TEXT, {
      acceptNode: (n) => (n.parentElement && n.parentElement.closest('.katex-mathml, .code-copy, style') ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT),
    });
    let n;
    while ((n = walker.nextNode())) {
      const t = n.nodeValue.toLocaleLowerCase();
      let i = 0;
      while ((i = t.indexOf(ql, i)) !== -1) {
        const r = document.createRange();
        r.setStart(n, i);
        r.setEnd(n, Math.min(n.nodeValue.length, i + q.length));
        ranges.push(r);
        i += Math.max(1, q.length);
      }
    }
  }
  S.find.ranges = ranges;
  if (S.find.index >= ranges.length || jump) S.find.index = ranges.length ? 0 : -1;
  if (jump && ranges.length) {
    const top = previewPane.scrollTop;
    const idx = ranges.findIndex((r) => r.getBoundingClientRect().top - previewPane.getBoundingClientRect().top + top >= top);
    S.find.index = idx >= 0 ? idx : 0;
  }
  paintFind(jump);
}

function paintFind(scroll) {
  const { ranges, index } = S.find;
  $('find-count').textContent = ranges.length ? `${index + 1} из ${ranges.length}` : ($('find-input').value ? 'Не найдено' : '');
  if (window.CSS && CSS.highlights && window.Highlight) {
    CSS.highlights.set('mdv-find', new Highlight(...ranges));
    if (index >= 0 && ranges[index]) CSS.highlights.set('mdv-find-current', new Highlight(ranges[index]));
    else CSS.highlights.delete('mdv-find-current');
  }
  if (scroll && index >= 0 && ranges[index]) {
    const r = ranges[index].getBoundingClientRect();
    const pr = previewPane.getBoundingClientRect();
    if (r.top < pr.top + 40 || r.bottom > pr.bottom - 20) previewPane.scrollTop += r.top - pr.top - pr.height / 3;
  }
}

function findStep(dir) {
  const n = S.find.ranges.length;
  if (!n) return;
  S.find.index = (S.find.index + dir + n) % n;
  paintFind(true);
}

// ---------- Режимы, тема, настройки ----------

function setMode(mode) {
  if (!['view', 'split', 'edit'].includes(mode) || !view) return;
  const prev = S.mode;
  let editorLine = null;
  if (prev === 'view' && mode !== 'view') editorLine = previewTopLine();
  S.mode = mode;
  root.className = 'mode-' + mode + (S.tocVisible ? '' : ' no-toc') + (S.exporting ? ' exporting' : '');
  if (mode === 'view' && S.find.open === false) { /* ничего */ }
  if (mode !== 'view' && S.find.open) closeFind();
  if (editorLine != null) {
    const n = Math.min(view.state.doc.lines, editorLine + 1);
    requestAnimationFrame(() => {
      view.requestMeasure();
      view.dispatch({ effects: EditorView.scrollIntoView(view.state.doc.line(n).from, { y: 'start' }) });
    });
  } else if (mode === 'split') {
    requestAnimationFrame(() => syncPreviewToEditor());
  }
  if (mode !== 'view') setTimeout(() => view.focus(), 0);
  post({ type: 'modeChanged', mode });
}

function setTheme(effective) {
  S.effectiveTheme = effective === 'dark' ? 'dark' : 'light';
  applyTheme(S.effectiveTheme);
}

function applyTheme(t) {
  document.documentElement.setAttribute('data-theme', t);
  for (const l of document.querySelectorAll('link[data-theme]')) l.disabled = l.getAttribute('data-theme') !== t;
  if (view) view.dispatch({ effects: themeComp.reconfigure(editorTheme(t === 'dark')) });
  if (preview) { resetMermaid(); S.mermaidDone = renderMermaid(preview, S.renderToken).catch(() => {}); }
}

function applySettings(st) {
  if (!st) return;
  if (st.wrap != null && view) view.dispatch({ effects: wrapComp.reconfigure(st.wrap ? EditorView.lineWrapping : []) });
  if ((st.editorFont != null || st.editorFontSize != null) && view) {
    S.editorFont = st.editorFont ?? S.editorFont;
    S.editorFontSize = st.editorFontSize ?? S.editorFontSize;
    view.dispatch({ effects: fontComp.reconfigure(fontTheme(S.editorFont, S.editorFontSize)) });
  }
  if (st.zoom != null) { S.zoom = st.zoom; document.documentElement.style.setProperty('--preview-zoom', String(st.zoom)); }
  if (st.fullWidth != null) document.documentElement.classList.toggle('full-width', !!st.fullWidth);
  if (st.tocVisible != null) { S.tocVisible = !!st.tocVisible; root.classList.toggle('no-toc', !S.tocVisible); }
  if (st.tocWidth) document.documentElement.style.setProperty('--toc-w', Math.round(st.tocWidth) + 'px');
  if (st.split) document.documentElement.style.setProperty('--split', String(st.split));
}

// ---------- Экспорт ----------

function waitImages(timeoutMs) {
  const imgs = [...preview.querySelectorAll('img')].filter((i) => !i.complete);
  if (!imgs.length) return Promise.resolve();
  return Promise.race([
    Promise.all(imgs.map((i) => new Promise((r) => { i.addEventListener('load', r, { once: true }); i.addEventListener('error', r, { once: true }); }))),
    new Promise((r) => setTimeout(r, timeoutMs)),
  ]);
}

async function beginExport() {
  S.exporting = true;
  root.classList.add('exporting');
  applyTheme('light');
  try { await S.mermaidDone; } catch (_) { /* ошибки уже показаны */ }
  await renderMermaid(preview, S.renderToken).catch(() => {});
  await waitImages(8000);
  if (document.fonts && document.fonts.ready) await document.fonts.ready;
  await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));
  post({ type: 'exportReady' });
}

function endExport() {
  S.exporting = false;
  root.classList.remove('exporting');
  applyTheme(S.effectiveTheme);
}

function getExportHtml() {
  const clone = preview.cloneNode(true);
  for (const b of clone.querySelectorAll('.code-copy, .mdv-find')) b.remove();
  for (const el of clone.querySelectorAll('[data-line]')) el.removeAttribute('data-line');
  for (const el of clone.querySelectorAll('[data-src]')) el.removeAttribute('data-src');
  return clone.innerHTML;
}

// ---------- Клавиатура ----------

const HOST_KEYS = new Set([
  'Ctrl+O', 'Ctrl+N', 'Ctrl+S', 'Ctrl+Shift+S', 'Ctrl+W', 'Ctrl+Tab', 'Ctrl+Shift+Tab', 'Ctrl+1', 'Ctrl+2', 'Ctrl+3',
  'Ctrl+P', 'Ctrl+Shift+O', 'Ctrl+Plus', 'Ctrl+Minus', 'Ctrl+0', 'Ctrl+Comma', 'Ctrl+F4', 'Ctrl+PageDown', 'Ctrl+PageUp',
]);

function keyName(e) {
  let k;
  const c = e.code || '';
  if (/^Key[A-Z]$/.test(c)) k = c.slice(3);
  else if (/^Digit\d$/.test(c)) k = c.slice(5);
  else if (/^Numpad\d$/.test(c)) k = c.slice(6);
  else if (c === 'Equal' || c === 'NumpadAdd') k = 'Plus';
  else if (c === 'Minus' || c === 'NumpadSubtract') k = 'Minus';
  else if (c === 'Comma') k = 'Comma';
  else k = e.key;
  const parts = [];
  if (e.ctrlKey || e.metaKey) parts.push('Ctrl');
  if (e.altKey) parts.push('Alt');
  if (e.shiftKey && k !== 'Plus') parts.push('Shift');
  parts.push(k);
  return parts.join('+');
}

function onKeyDown(e) {
  if (e.isComposing) return;
  const k = keyName(e);
  if (HOST_KEYS.has(k)) {
    e.preventDefault();
    e.stopPropagation();
    if (k === 'Ctrl+S' || k === 'Ctrl+Shift+S') flushText();
    post({ type: 'shortcut', key: k });
    return;
  }
  if (k === 'Ctrl+F' || k === 'Ctrl+H') {
    e.preventDefault();
    e.stopPropagation();
    if (S.mode === 'view') openFind();
    else if (k === 'Ctrl+H') openReplace(view);
    else openSearchPanel(view);
    return;
  }
  if (k === 'Escape' && S.find.open) { e.preventDefault(); closeFind(); return; }
  if (k === 'F3' || k === 'Shift+F3') {
    if (S.mode === 'view' && S.find.open) { e.preventDefault(); findStep(k === 'F3' ? 1 : -1); }
  }
  // Блокировать перезагрузку и печать браузера
  if (k === 'F5' || k === 'Ctrl+R') e.preventDefault();
}

function flushText() {
  if (S.pendingTimer) { clearTimeout(S.pendingTimer); sendText(); }
}

// ---------- Разделители ----------

function setupSplitter(el, onMove, onEnd) {
  el.addEventListener('pointerdown', (e) => {
    e.preventDefault();
    el.setPointerCapture(e.pointerId);
    document.body.classList.add('dragging');
    const move = (ev) => onMove(ev);
    const up = () => {
      el.removeEventListener('pointermove', move);
      el.removeEventListener('pointerup', up);
      el.removeEventListener('pointercancel', up);
      document.body.classList.remove('dragging');
      onEnd();
    };
    el.addEventListener('pointermove', move);
    el.addEventListener('pointerup', up);
    el.addEventListener('pointercancel', up);
  });
}

// ---------- Сообщения от приложения ----------

function handle(msg) {
  if (!msg || typeof msg !== 'object') return;
  switch (msg.type) {
    case 'init':
      applySettings(msg.settings);
      setTheme(msg.theme);
      setText(msg.text || '', false);
      render(msg.html || '', msg.toc || [], msg.version);
      setMode(msg.mode || 'view');
      if (msg.mode === 'view') previewPane.scrollTop = 0;
      break;
    case 'render':
      render(msg.html || '', msg.toc || [], msg.version);
      break;
    case 'setText':
      setText(msg.text || '', !!msg.keepScroll);
      break;
    case 'mode':
      setMode(msg.mode);
      break;
    case 'theme':
      setTheme(msg.theme);
      break;
    case 'settings':
      applySettings(msg.settings);
      break;
    case 'insert':
      if (S.mode === 'view') setMode('split');
      insertText(msg.text || '');
      break;
    case 'command':
      runCommand(msg.name);
      break;
    case 'beginExport':
      beginExport();
      break;
    case 'endExport':
      endExport();
      break;
    case 'anchor':
      if (msg.id) scrollToAnchor(String(msg.id));
      break;
    case 'version':
      S.version = Math.max(S.version, msg.version || 0);
      break;
    default:
      break;
  }
}

function runCommand(name) {
  if (formatCommands[name]) {
    if (S.mode === 'view') setMode('split');
    formatCommands[name]();
    return;
  }
  switch (name) {
    case 'find': if (S.mode === 'view') openFind(); else openSearchPanel(view); break;
    case 'replace': if (S.mode === 'view') setMode('split'); openReplace(view); break;
    case 'undo': view.focus(); cmUndo(view); break;
    case 'redo': view.focus(); cmRedo(view); break;
    case 'focus': if (S.mode === 'view') previewPane.focus(); else view.focus(); break;
    case 'closeFind': closeFind(); closeSearchPanel(view); break;
    default: break;
  }
}

// ---------- Запуск ----------

function init() {
  root = $('root');
  previewPane = $('preview-pane');
  preview = $('preview');
  tocEl = $('toc');
  tocList = $('toc-list');
  editorHost = $('editor');
  createEditor();

  previewPane.addEventListener('scroll', scheduleTocHighlight, { passive: true });
  preview.addEventListener('click', (e) => {
    const btn = e.target.closest('.code-copy');
    if (btn) { e.preventDefault(); copyCode(btn); return; }
    const a = e.target.closest('a');
    if (a) { e.preventDefault(); handleLink(a); }
  });
  preview.addEventListener('auxclick', (e) => {
    const a = e.target.closest('a');
    if (a) { e.preventDefault(); handleLink(a); }
  });
  tocList.addEventListener('click', (e) => {
    const li = e.target.closest('.toc-item');
    if (li) scrollToHeading(Number(li.dataset.index));
  });

  for (const b of document.querySelectorAll('#fmt-toolbar button[data-cmd]')) {
    b.addEventListener('mousedown', (e) => e.preventDefault());
    b.addEventListener('click', () => runCommand(b.dataset.cmd));
  }

  $('find-input').addEventListener('input', () => runFind(true));
  $('find-input').addEventListener('keydown', (e) => {
    if (e.key === 'Enter') { e.preventDefault(); findStep(e.shiftKey ? -1 : 1); }
  });
  $('find-next').addEventListener('click', () => findStep(1));
  $('find-prev').addEventListener('click', () => findStep(-1));
  $('find-close').addEventListener('click', closeFind);

  setupSplitter($('splitter'), (e) => {
    const main = $('main').getBoundingClientRect();
    const f = Math.min(0.85, Math.max(0.15, (e.clientX - main.left) / main.width));
    document.documentElement.style.setProperty('--split', String(f));
  }, () => {
    post({ type: 'layout', split: Number(getComputedStyle(document.documentElement).getPropertyValue('--split')) || 0.5 });
  });
  setupSplitter($('toc-splitter'), (e) => {
    const w = Math.min(600, Math.max(120, e.clientX));
    document.documentElement.style.setProperty('--toc-w', w + 'px');
  }, () => {
    post({ type: 'layout', tocWidth: tocEl.getBoundingClientRect().width });
  });

  window.addEventListener('keydown', onKeyDown, true);
  window.addEventListener('wheel', (e) => {
    if (e.ctrlKey) {
      e.preventDefault();
      post({ type: 'shortcut', key: e.deltaY < 0 ? 'Ctrl+Plus' : 'Ctrl+Minus' });
    }
  }, { passive: false, capture: true });

  const hasFiles = (e) => e.dataTransfer && [...e.dataTransfer.types].includes('Files');
  window.addEventListener('dragover', (e) => {
    if (hasFiles(e)) { e.preventDefault(); e.stopPropagation(); e.dataTransfer.dropEffect = 'copy'; }
  }, true);
  window.addEventListener('drop', (e) => {
    if (!hasFiles(e)) return;
    e.preventDefault();
    e.stopPropagation();
    if (webview && webview.postMessageWithAdditionalObjects) webview.postMessageWithAdditionalObjects({ type: 'dropFiles' }, e.dataTransfer.files);
  }, true);

  window.addEventListener('resize', () => { scheduleSync(); scheduleTocHighlight(); });

  if (webview) webview.addEventListener('message', (e) => handle(e.data));
  post({ type: 'ready' });
}

// Доступ для приложения и автотестов
window.mdv = {
  handle,
  outbox,
  getText: () => (view ? view.state.doc.toString() : ''),
  flush: () => flushText(),
  getExportHtml,
  getPreviewText: () => preview.innerText,
  getPreviewHtml: () => preview.innerHTML,
  getState: () => ({
    mode: S.mode,
    theme: document.documentElement.getAttribute('data-theme'),
    previewScroll: previewPane.scrollTop,
    editorScroll: view ? view.scrollDOM.scrollTop : 0,
    tocCurrent: Number((tocList.querySelector('.toc-item.current') || { dataset: { index: -1 } }).dataset.index),
    tocItems: S.toc.length,
    renderedVersion: S.renderedVersion,
    version: S.version,
    mermaidOk: preview.querySelectorAll('.mermaid-ok').length,
    mermaidErrors: preview.querySelectorAll('.mermaid-error').length,
    mermaidPending: preview.querySelectorAll('div.mermaid:not(.mermaid-ok):not(.mermaid-error)').length,
    exporting: S.exporting,
    mathOk: preview.querySelectorAll('.math-ok').length,
    mathErrors: preview.querySelectorAll('.math-error').length,
    highlighted: preview.querySelectorAll('code.hljs').length,
    zoom: S.zoom,
  }),
  scrollToHeading,
  typeText: (text) => insertText(text),
  setTextAsUser: (text) => {
    view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: text }, userEvent: 'input' });
  },
  focusEditor: () => view && view.focus(),
  cursorToEnd: () => {
    view.dispatch({ selection: { anchor: view.state.doc.length }, scrollIntoView: true });
    view.focus();
  },
  scrollPreviewTo: (y) => { previewPane.scrollTop = y; },
  scrollEditorToLine: (n) => {
    const ln = Math.min(view.state.doc.lines, Math.max(1, n));
    view.dispatch({ effects: EditorView.scrollIntoView(view.state.doc.line(ln).from, { y: 'start' }) });
  },
  mermaidReady: () => S.mermaidDone,
};

if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
else init();
