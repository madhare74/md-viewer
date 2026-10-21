// Дымовой тест страницы в jsdom: подсветка, формулы, ссылки, оглавление, команды форматирования.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { JSDOM } from 'jsdom';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const www = process.env.MDV_WWW || join(here, '..', '..', 'src', 'MdViewer', 'wwwroot');

async function load() {
  let html = readFileSync(join(www, 'page.html'), 'utf8');
  html = html.replace(/<script[^>]*><\/script>/g, '').replace(/<link[^>]*>/g, '');
  const dom = new JSDOM(html, { runScripts: 'outside-only', pretendToBeVisual: true, url: 'https://app.mdviewer.example/page.html' });
  const w = dom.window;
  const sent = [];
  const listeners = [];
  w.chrome = { webview: { postMessage: (m) => sent.push(JSON.parse(JSON.stringify(m))), addEventListener: (t, f) => listeners.push(f) } };
  w.Range.prototype.getClientRects = () => ({ length: 0, item: () => null, [Symbol.iterator]: function* () {} });
  w.Range.prototype.getBoundingClientRect = () => ({ top: 0, left: 0, right: 0, bottom: 0, width: 0, height: 0 });
  w.document.elementFromPoint = () => null;
  w.Element.prototype.scrollIntoView = () => {};
  w.eval(readFileSync(join(www, 'lib', 'app.bundle.js'), 'utf8'));
  const send = (data) => listeners.forEach((f) => f({ data }));
  if (!w.mdv || w.document.readyState === 'loading') await new Promise((r) => setTimeout(r, 30));
  return { w, sent, send };
}

const sampleHtml = `<h1 id="заголовок" data-line="0">Заголовок</h1>
<p data-line="2">Текст <a href="#заголовок">якорь</a> <a href="https://example.com/x">внешняя</a> <a href="other.md#part">файл</a></p>
<pre data-line="4"><code class="language-python">def f(x):
    return x * 2
</code></pre>
<p data-line="8"><span class="math">\\(x^2 + y^2\\)</span></p>
<div class="math" data-line="10">\\[\\frac{a}{b}\\]</div>
<p data-line="12"><span class="math">\\(\\frac{a}{\\)</span></p>`;

test('страница отправляет ready и рендерит документ', async () => {
  const { w, sent, send } = await load();
  assert.ok(sent.some((m) => m.type === 'ready'));
  send({ type: 'init', text: '# Заголовок\n', html: sampleHtml, toc: [{ level: 1, text: 'Заголовок', id: 'заголовок', line: 0 }], mode: 'view', theme: 'dark', settings: { wrap: true, zoom: 1.2, tocVisible: true } });
  const st = w.mdv.getState();
  assert.equal(st.mode, 'view');
  assert.equal(st.theme, 'dark');
  assert.equal(st.highlighted, 1, 'подсветка кода');
  assert.equal(st.mathOk, 2, 'формулы');
  assert.equal(st.mathErrors, 1, 'ошибка в формуле показана');
  assert.match(w.document.querySelector('.math-error').textContent, /Ошибка в формуле/);
  assert.equal(w.document.querySelectorAll('.code-copy').length, 1);
  assert.equal(w.document.querySelectorAll('#toc-list .toc-item').length, 1);
  assert.equal(w.mdv.getText(), '# Заголовок\n');
});

test('ссылки: внешняя и на другой .md уходят в приложение', async () => {
  const { w, sent, send } = await load();
  send({ type: 'init', text: '', html: sampleHtml, toc: [], mode: 'view', theme: 'light', settings: {} });
  const links = w.document.querySelectorAll('#preview a');
  links[1].click();
  links[2].click();
  const ext = sent.find((m) => m.type === 'openExternal');
  const loc = sent.find((m) => m.type === 'openLocal');
  assert.equal(ext.url, 'https://example.com/x');
  assert.equal(loc.path, 'other.md');
  assert.equal(loc.hash, 'part');
});

test('правка текста отправляет textChanged, сохранение — shortcut', async () => {
  const { w, sent, send } = await load();
  send({ type: 'init', text: 'abc', html: '<p>abc</p>', toc: [], mode: 'split', theme: 'light', settings: {} });
  w.mdv.setTextAsUser('hello **world**');
  await new Promise((r) => setTimeout(r, 200));
  const tc = sent.filter((m) => m.type === 'textChanged').pop();
  assert.equal(tc.text, 'hello **world**');
  const ev = new w.KeyboardEvent('keydown', { code: 'KeyS', key: 'ы', ctrlKey: true, bubbles: true });
  w.document.dispatchEvent(ev);
  assert.ok(sent.some((m) => m.type === 'shortcut' && m.key === 'Ctrl+S'), 'Ctrl+S в русской раскладке');
});

test('команды форматирования', async () => {
  const { w, send } = await load();
  send({ type: 'init', text: 'слово', html: '', toc: [], mode: 'split', theme: 'light', settings: {} });
  w.mdv.handle({ type: 'command', name: 'list' });
  assert.equal(w.mdv.getText(), '- слово');
  w.mdv.handle({ type: 'command', name: 'task' });
  assert.equal(w.mdv.getText(), '- [ ] слово');
  w.mdv.handle({ type: 'command', name: 'quote' });
  assert.equal(w.mdv.getText(), '> слово');
  w.mdv.handle({ type: 'command', name: 'heading' });
  assert.equal(w.mdv.getText(), '# > слово');
  w.mdv.handle({ type: 'insert', text: 'X' });
  assert.ok(w.mdv.getText().includes('X'));
});

test('экспорт: html без кнопок копирования и служебных атрибутов', async () => {
  const { w, send } = await load();
  send({ type: 'init', text: '', html: sampleHtml, toc: [], mode: 'view', theme: 'light', settings: {} });
  const h = w.mdv.getExportHtml();
  assert.ok(!h.includes('code-copy'));
  assert.ok(!h.includes('data-line'));
  assert.ok(h.includes('katex'));
});

test('блок mermaid превращается в контейнер схемы', async () => {
  const { w, send } = await load();
  send({ type: 'init', text: '', html: '<pre><code class="language-mermaid" data-line="3">graph TD\nA--&gt;B[&lt;b&gt;x&lt;/b&gt;]\n</code></pre>', toc: [], mode: 'view', theme: 'light', settings: {} });
  const div = w.document.querySelector('#preview div.mermaid');
  assert.ok(div);
  assert.equal(div.getAttribute('data-line'), '3');
  assert.ok(div.textContent.includes('A-->B[<b>x</b>]'));
  assert.equal(w.document.querySelectorAll('#preview .code-copy').length, 0);
});
