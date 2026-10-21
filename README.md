# MD Viewer

Приложение для Windows: просмотр и редактирование файлов Markdown (`.md`) с оформлением в стиле GitHub.

- Вкладки, режимы «Просмотр» / «Правка и просмотр» / «Правка» (Ctrl+1/2/3), оглавление (Ctrl+Shift+O).
- Таблицы, списки задач, сноски, эмодзи, подсветка кода, формулы KaTeX, схемы Mermaid — всё встроено и работает без интернета.
- Экспорт в PDF и самодостаточный HTML, печать.
- Светлая и тёмная тема, масштаб (Ctrl+колёсико).

## Установка

Готовый установщик `MDViewer-Setup.exe` и portable-архив лежат в ветке [`build`](../../tree/build).
Установка не требует прав администратора. При первом запуске Windows SmartScreen может предупредить
о неизвестном издателе: «Подробнее» → «Выполнить в любом случае».

## Сборка

Сборка и тесты выполняются в GitHub Actions (`.github/workflows/build.yml`):

1. `web/` — встроенная веб-часть (CodeMirror, KaTeX, Mermaid, highlight.js), `npm ci && npm run build`.
2. `dotnet build MdViewer.sln`, unit-тесты `tests/MdViewer.Core.Tests`.
3. `dotnet publish` (self-contained, win-x64), portable-архив, установщик Inno Setup (`installer/`).
4. UI-тесты `tests/MdViewer.UiTests` (FlaUI) со скриншотами.

Результаты публикуются в ветки:

| Ветка | Содержимое |
| --- | --- |
| `build` | `MDViewer-Setup.exe`, `MDViewer-portable.zip`, `COMMIT` |
| `ui-test-results` | результаты тестов, скриншоты, образцы экспорта PDF/HTML |
| `ci-logs-build` | журналы шагов сборки |

## Устройство

- `src/MdViewer.Core` — кодировки и переводы строк, разбор Markdown (Markdig), оглавление, настройки, экспорт HTML.
- `src/MdViewer` — WPF (.NET 8) + WebView2: окно, вкладки, меню, диалоги, связь с файлами.
- `web/` — страница документа: редактор, просмотр, поиск, синхронная прокрутка.

Настройки хранятся в `%AppData%\MD Viewer\settings.json`.
