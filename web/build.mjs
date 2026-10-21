// Сборка встроенной веб-части: бандл редактора и страницы + копирование стилей, KaTeX, Mermaid.
// Результат кладётся в src/MdViewer/wwwroot (или в каталог из аргумента).
import { build } from 'esbuild';
import { cpSync, mkdirSync, readdirSync, rmSync, copyFileSync, existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const out = resolve(process.argv[2] || join(here, '..', 'src', 'MdViewer', 'wwwroot'));
const lib = join(out, 'lib');
const nm = join(here, 'node_modules');

rmSync(out, { recursive: true, force: true });
mkdirSync(join(lib, 'katex', 'fonts'), { recursive: true });
mkdirSync(join(lib, 'mermaid'), { recursive: true });

await build({
  entryPoints: [join(here, 'src', 'main.js')],
  bundle: true,
  minify: true,
  format: 'iife',
  target: ['chrome110'],
  legalComments: 'none',
  outfile: join(lib, 'app.bundle.js'),
  logLevel: 'warning',
});

copyFileSync(join(here, 'src', 'page.html'), join(out, 'page.html'));
copyFileSync(join(here, 'src', 'app.css'), join(lib, 'app.css'));
copyFileSync(join(nm, 'github-markdown-css', 'github-markdown-light.css'), join(lib, 'github-markdown-light.css'));
copyFileSync(join(nm, 'github-markdown-css', 'github-markdown-dark.css'), join(lib, 'github-markdown-dark.css'));
copyFileSync(join(nm, 'highlight.js', 'styles', 'github.css'), join(lib, 'hljs-github.css'));
copyFileSync(join(nm, 'highlight.js', 'styles', 'github-dark.css'), join(lib, 'hljs-github-dark.css'));
copyFileSync(join(nm, 'katex', 'dist', 'katex.min.css'), join(lib, 'katex', 'katex.min.css'));
for (const f of readdirSync(join(nm, 'katex', 'dist', 'fonts'))) {
  // Достаточно woff2 — WebView2 (Chromium) его поддерживает; так меньше размер.
  if (f.endsWith('.woff2')) copyFileSync(join(nm, 'katex', 'dist', 'fonts', f), join(lib, 'katex', 'fonts', f));
}
copyFileSync(join(nm, 'mermaid', 'dist', 'mermaid.min.js'), join(lib, 'mermaid', 'mermaid.min.js'));

for (const f of ['LICENSE']) {
  if (existsSync(join(here, '..', f))) cpSync(join(here, '..', f), join(out, f));
}
console.log('web assets ->', out);
