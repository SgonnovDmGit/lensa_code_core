# Client API — Tool surface (наборы тулов по продуктам)

> Часть client-контракта. Индекс: [client-api.md](client-api.md).

## Tool surface for lensa_universal (swan_code_universal)

Продукт `lensa_universal` (LenSa Universal — публичный универсальный кодинг-клиент, REQ-018, v0.60.1) — **«чистый чат без тулов»**: tool-set продукта пуст, `tools` в запрос к AI не отправляются, инструкции tool_use в системный промпт не подмешиваются. В ответах `toolUses` / `codeChanges` / `executeCommands` всегда `[]`. Любой присланный `tool_result` отклоняется `400 UNKNOWN_TOOL`. Полноценный tool-surface для Universal — отдельным REQ клиента (v0.14.0+ на его стороне).

## Tool surface for default (swan_code_client)

Default-продукт (1С-кодинг-клиент) использует **гибридный** tool-surface: **2 legacy-тула** (`apply_code_change`, `execute_command`) уходят клиенту в типизированных DTO-массивах `codeChanges` / `executeCommands` (см. выше); **6 onec_* тулов** (REQ-017, typed с v0.65.0) + **4 window-тула** (REQ-019, v0.64.0, typed) + **2 процедурных тула** (REQ-024, v0.79.0, typed) + **2 тула доски задач** (`task_board_update` REQ-021 v0.75.0 + `list_oneC_task_boards` REQ-029 v0.81.0) — через generic-канал `toolUses[]` с `name + dataB64` payload. Итого AI видит 16 тулов, все с typed unpacker'ами.

### Per-model гейт `supports_tools` (T-000142, v0.78.0)

Сверх продукта и режима tool-surface хода гейтится **возможностями модели**: если модель по Router-каталогу не поддерживает native tool_use (`supportsTools: false` в `GET /v1/api/models`, см. [client-api-reads.md](client-api-reads.md)), сервер **не кладёт tools в запрос** — на всём turn-семействе (`/chat`, `/chat/async`, `/chat/tool-results[/async]`, `/chat/tool-retry`). Ход проходит, но AI отвечает только текстом; сессия, **начатая** на такой модели, получает текстовые fallback-инструкции вместо tool-инструкций в system prompt. Переключение на без-туловую модель **посреди** сессии промпт не меняет (он заморожен) — модель может описывать «вызовы» текстом; клиенту рекомендуется дизейблить такие модели в пикере tool-продукта по флагу `supportsTools`. Fail-open: поле неизвестно → состав запроса прежний. Capability-флаг: `tools-gate` в `features` `GET /v1/admin/server-info`.

### Режимы работы `assistMode` (F-000041, REQ-022, v0.77.0)

Опциональное поле `assistMode` (`"planning"` | `"auto"` | `"review"`) на **всём turn-семействе**: `POST /v1/api/chat`, `/chat/async`, `/chat/tool-results[/async]`, `/chat/tool-retry`. Управляет тем, насколько AI имеет право менять состояние конфигуратора — гейт на **сервере**, до вызова провайдера: модель не видит запрещённые тулы и не пытается их звать (вместо отказа постфактум).

| Режим | Surface | Prompt-добавка |
|---|---|---|
| `auto` (или поле отсутствует) | полный набор (как до v0.77.0) | нет |
| `planning` | без write-тулов: `apply_oneC_edit`, `apply_code_change`, `execute_command` не декларируются; read-тулы и `task_board_update` остаются | «изучай и планируй, правок не вноси, веди доску задач» |
| `review` | `apply_oneC_edit` с суженной схемой: `target` — enum с единственным значением `"sandbox"`, `windowId` убран; остальное как auto | «правки только в песочницу на ревью человеку» |

Правила:
- **Per-turn, без наследования сервером:** клиент шлёт `assistMode` в **каждом** запросе хода (включая tool-results-продолжения) — континуация без поля идёт как `auto`. Async-ход — исключение: режим сабмита персистится в тикете, server-tool пере-сабмиты на поллинге сохраняют surface режима.
- **Невалидное значение = `auto` + warn в лог** — режим не роняет чат (400 не возвращается).
- **Только `swan_code_client`** (default-продукт): для `query_console_1c` / `lensa_universal` поле игнорируется (surface и промпт без изменений).
- Валидация входящих `tool_result` — mode-нейтральна: результат write-тула, выданного в auto-ходе, принимается и в planning-ходе (`UNKNOWN_TOOL` не возникает из-за смены режима).
- Prompt-добавка — transient-инжект в контекст хода (не в персистируемый system prompt сессии).
- Capability-флаг: `assist-mode` в `features` `GET /v1/admin/server-info`.

### Negotiation clientTools (F-000042, REQ-057, v0.80.0)

Опциональное поле `clientTools: string[]` на **всём turn-семействе** (`/chat`, `/chat/async`,
`/chat/stream`, `/chat/tool-results[/async]`, `/chat/tool-retry`): клиент объявляет
canonical-имена **client-executed** тулов, которые он умеет исполнить. Определения и
JSON-схемы остаются серверными — клиент объявляет только имена. Эффективный surface хода:

```text
server-executed тулы (всегда)
+ пересечение(серверный каталог client-executed продукта, clientTools)
```

Правила:
- **Ключ отсутствует (legacy fail-open):** полный client-executed каталог продукта —
  byte-for-byte прежнее поведение, старые клиенты ничего не замечают.
- **`[]` (присутствует пустой):** ноль client-executed тулов; server-executed
  (`load_skill`, `get_latest_query_version`, `recall`) остаются.
- **Классификация:** server-executed тулы полем не управляются и клиентом не объявляются.
  Server-resolved `replace_query_fragment` (сервер считает дифф, клиент применяет fullText) —
  с точки зрения capability **client-executed**: объявляйте его, если умеете применять.
- **Мусор не роняет запрос:** неизвестные имена и дубли игнорируются; клиентский вход
  не может создать тул вне серверного каталога и не может расширить surface (гейты
  продукта / `assistMode` / `supports_tools`-модели применяются ДО пересечения).
- **Кап 256 имён:** имена сверх капа **игнорируются** (warn в серверный лог, НЕ 400) —
  fail-open по духу поля; честный клиент объявляет только свой рантайм и до капа не
  доходит. Зафиксировано здесь на случай будущих разборов: если клиент прислал >256
  имён, эффективный набор может оказаться уже заявленного — сервер никогда не шире.
- **Тул-связки (сужение на сервере):** кормящие буфер тулы консоли —
  `replace_table` / `insert_table` / `delete_table` / `replace_query_fragment` —
  осмыслены только вместе с применяющим `apply_query_change`. Объявленный write-тул
  без applier'а в том же наборе **выкидывается из surface** (warn в лог): недоступный
  буфер повисал бы и глушил авто-компактизацию. Объявляйте согласованные наборы —
  модель не получает подсказок о необъявленных тулах.
- **Per-request, без наследования:** продолжение хода (`tool-results`, `tool-retry`) шлёт
  своё поле — так существующую сессию безопасно резюмирует клиент другой версии.
  **Async-ход** снапшотит поле на submit'е: server-tool пере-сабмиты на поллинге
  используют снапшот тикета, поллинг ничего не пересчитывает; `tool-results/async`
  задаёт новый снапшот нового тикета.
- `/chat/stream` — text-only путь (тулы в стрим не декларируются): поле принимается
  и игнорируется.

### Legacy (apply_code_change, execute_command)

См. описание `codeChanges` / `executeCommands` в `POST /v1/api/chat` выше. Возврат от клиента — через legacy-поля в `ToolResultItem` (`failureType`, `message`, `contextB64`), `name` опционален.

### onec_* (typed с v0.65.0 — REQ-017 T-000063)

UIA-операции в 1С-конфигураторе. Payload-shapes финализированы клиентом (REQ-017), сервер разворачивает `dataB64` в LLM-проекцию typed unpacker'ами. **Failure-envelope onec_*** — `{"error":"<код>","details?":"<текст>"}`, коды `not_found` / `uia_failed` / `access_denied` / `not_implemented` / `timeout` / `invalid_input` (unpacker различает success/failure по непустому `error`).

**Tool — `open_oneC_object(path)`**: открыть объект конфигурации по пути (`Справочник.Контрагенты`, `Документы.X.Форма.Y`).
- success: `{"opened":true,"activated":true,"windowTitle":"Справочник Контрагенты"}`.

**Tool — `read_oneC_configuration_tree(scope?)`**: дерево конфигурации. `scope` (опц.) — поддерево объекта `Тип.Имя`; без него — всё дерево.
- success: `{"scope":"Справочник.Контрагенты","tree":[{"name","kind","type?","path","children?","synonym?"}],"truncated":false}`. `kind` ∈ `object`/`subsystem`/`commonModule`/`attribute`/`tabularSection`/`form`/`layout`/`command`/`enumValue`. `truncated:true` — частичное дерево (уточнить через `scope`).

**Tool — `read_oneC_messages_panel()`**: содержимое нижней панели «Сообщения» (без параметров).
- success: `{"lines":[{"text":"Модуль(42,3): Синтаксическая ошибка","severity":"error"}],"total":2}`. `severity` ∈ `error`/`warning`/`info` (детектит клиент по префиксу).

**Tool — `run_oneC_module_check(moduleRuntimeId?)`**: «Проверка модуля». Клиент сам дочитывает панель — результат одним payload'ом (экономит хоп).
- success: `{"passed":true,"messagesRead":2,"messages":[{"text":"...","severity":"warning","line":84}]}`. `passed=true` ⟺ среди `messages` нет `severity=="error"`; `line` опц.

**Tool — `oneC_debug_action(action, line?)`**: debug-действия. `action` — string (не enum); ожидаемые значения `save_and_close`, `start_debug`, `toggle_breakpoint` (требует `line`), `step_over`, `step_into`, `continue`.
- success: `{"applied":true,"state":"at_line_43"}`. `state` специфичен для action (`saved`/`already_saved`, `started`/`already_running`, `breakpoint_added`/`breakpoint_removed`, `at_line_<N>`); опционален.

**Tool — `add_oneC_attribute(object, name, type, required?, synonym?)`**: добавить реквизит через UIA-диалог. (Входные поля выровнены с v0.65.0: были `objectPath`/`attributeName`/`attributeType`.)
- success: `{"added":true,"savedByAutosave":true}`. `savedByAutosave:false` → требуется явный `oneC_debug_action(action:"save_and_close")`. `runtimeId` не возвращается (1С не выдаёт стабильный ID реквизита через UIA — проверка через повторный `read_oneC_configuration_tree(scope)`).

**Возврат от клиента (для всех 6 тулов):** `name = "<tool-name>"` + `dataB64 = "<base64-JSON>"`, `status: success|failure`. Сервер разворачивает в LLM-проекцию (`OPEN_OBJECT_OK` / `ДЕРЕВО КОНФИГУРАЦИИ ...` / `ПАНЕЛЬ СООБЩЕНИЙ ...` / `ПРОВЕРКА МОДУЛЯ ...` / `DEBUG_ACTION_OK` / `ADD_ATTRIBUTE_OK` и `*_FAILED: <код> — <details>`). На клиенте v0.11.2 — stub-handlers (возвращают failure), реальные UIA-handlers — v0.14.0+; серверная сторона на shape'ы уже готова.

### window-тулы (REQ-019, v0.64.0 — typed)

Работа AI с **текущим окном** конфигуратора: список открытых окон → чтение активного → переключение → точечные правки. Дополняют onec_* (не заменяют). В отличие от onec_* — payload-shapes финализированы, серверный unpacker сразу typed (не PLACEHOLDER). Все 4 — client-executed (дифф `apply_oneC_edit` резолвит клиент). Envelope: `name` + `dataB64` (base64-JSON), `status: success|failure`; failure payload всегда `{"error":"<КОД>","message":"<текст>", ...опц.}`.

**Tool — `list_oneC_windows()`** (без параметров): список открытых окон редакторов кода.
- success: `{"configurator":{"title":"Конфигуратор - УправлениеТорговлей","connected":true},"windows":[{"id":"w1","title":"Справочник Контрагенты: МодульОбъекта","active":true,"lines":214},{"id":"w2","title":"ОбщийМодуль ОбменДанными: Модуль","active":false,"lines":1050}]}`. `id` — стабильный в рамках сессии идентификатор окна (используется в activate/apply); `active` — окно в фокусе; `lines` — размер, чтобы AI оценивал стоимость чтения.
- failure: `{"error":"NO_CONFIGURATOR","message":"..."}`.

**Tool — `read_oneC_active_window()`** (без параметров): текст активного окна.
- success: `{"id":"w1","title":"...","text":"<полный текст модуля>","lines":214,"truncated":false}`. При превышении клиентского лимита размера — `"truncated":true` + `"note"` с пояснением, какая часть отдана.
- failure: `{"error":"NO_ACTIVE_WINDOW",...}` (активно не окно кода) | `NO_CONFIGURATOR`.
- **REQ-024 (v0.79.0):** описание тула для AI переформулировано — для больших модулей клиент вправе вернуть **карту процедур вместо текста**; модель направляется в `list_oneC_procedures` / `read_oneC_procedure` (см. «процедурные тулы» ниже). Порядок на клиентской стороне: сначала декларация (эта), потом смена дефолта выдачи.

**Tool — `activate_oneC_window(windowId)`**: переключиться на окно и сразу вернуть его текст (один раунд вместо activate+read).
- input: `{"windowId":"w2"}`.
- success: тот же shape, что у `read_oneC_active_window` (окно уже активировано).
- failure: `{"error":"WINDOW_NOT_FOUND","message":"Нет окна w9. Доступны: w1, w2"}` | `NO_CONFIGURATOR`.

**Tool — `apply_oneC_edit(target, windowId?, edits[])`**: точечные правки в окно конфигуратора или клиентскую песочницу.
- input: `{"target":"window","windowId":"w1","edits":[{"find":"<точный уникальный фрагмент>","replace":"<новый текст>"},{"append":"<текст в конец модуля>"}]}`. `target`: `"window"` (обязателен `windowId`) | `"sandbox"`. `edits[]` — массив; элемент одного из видов: `find`/`replace` (одиночное точное вхождение, `replace:""` = удаление), `procedure`/`replace` (REQ-024, v0.79.0 — замена процедуры **целиком** по имени из `list_oneC_procedures`; `replace` — полный новый текст процедуры, включая `Процедура`/`КонецПроцедуры`; взаимоисключим с `find`) или `append`; применяются строго последовательно. Режим `procedure` — **не отдельный тул** намеренно: гейтинг режимов REQ-022 навешен на `apply_oneC_edit` и наследуется (в `review` доступен внутри суженной sandbox-схемы).
- success: `{"applied":2,"windowId":"w1","markers":true}` — `markers` = расставлял ли клиент маркеры-комментарии изменений (детерминированно, клиентская настройка; формат маркеров AI знать не должен).
- failure: `{"error":"MATCH_NOT_FOUND","editIndex":0,"message":"..."}` | `AMBIGUOUS_MATCH` (фрагмент >1 раза) | `WINDOW_NOT_FOUND` | `SANDBOX_UNAVAILABLE` (песочница на клиенте с v0.13.1) | `EDIT_REJECTED` (режим клиента запретил запись).

**Возврат от клиента (для всех 4 window-тулов):** `name = "<tool-name>"` + `dataB64 = "<base64-JSON>"`. Сервер разворачивает в LLM-проекцию typed unpacker'ом (`LIST_WINDOWS_FAILED` / `АКТИВНОЕ ОКНО ...` / `APPLY_EDIT_OK` / `APPLY_EDIT_FAILED` и т.п.). Маркеры-комментарии (`++/--` + `[task]_[name]_[дата]`) расставляет клиент — серверу и AI формат знать не нужно.

### процедурные тулы (REQ-024, v0.79.0 — typed)

Точечное прицеливание к коду BSL-модуля: модули 1С routinely бывают на ~10к строк (~400 КБ), чтение целиком через `read_oneC_active_window` неподъёмно по токенам. Работа по процедурам: карта модуля → текст одной процедуры → замена одной процедуры (режим `procedure` в `apply_oneC_edit`, см. выше). Оба тула client-executed (парсит BSL оффлайновый парсер клиента, v0.18.0+); envelope тот же (`name` + `dataB64`, failure `{"error":"<КОД>","message":"<текст>", ...}`). Читают и окно, и песочницу — поле `target` (тот же enum, что у `apply_oneC_edit`; отсутствие = `window`).

**Tool — `list_oneC_procedures(target?)`**: карта процедур/функций модуля без текста кода.
- input: `{"target":"window"|"sandbox"}` (отсутствие поля = `window`).
- success: `{"procedures":[{"name":"ЗаполнитьРеквизиты","kind":"procedure"|"function","startLine":12,"endLine":88,"isExport":true,"signature":"Процедура ЗаполнитьРеквизиты(Объект) Экспорт"}],"lines":9800,"chars":401200}`. Строки 1-based, границы включительные.
- failure: `{"error":"NO_CONFIGURATOR"|"NO_ACTIVE_WINDOW"|...,"message":"..."}`.
- LLM-проекция: `КАРТА ПРОЦЕДУР МОДУЛЯ (N строк, M символов):` + строки `start-end [экспорт] <signature>` + хвост-подсказка про `read_oneC_procedure`.

**Tool — `read_oneC_procedure(name, target?)`**: текст одной процедуры/функции по имени.
- input: `{"name":"ЗаполнитьРеквизиты","target":"window"|"sandbox"}`.
- success: `{"name":"...","kind":"procedure"|"function","startLine":12,"endLine":88,"isExport":true,"text":"<полный текст процедуры>"}`.
- failure: `{"error":"PROCEDURE_NOT_FOUND","message":"...","available":["ИмяА","ИмяБ"]}` — `available` (опц.) — список доступных имён, сервер рендерит его модели, чтобы она поправила имя без повторного `list_oneC_procedures`.
- LLM-проекция: `ПРОЦЕДУРА|ФУНКЦИЯ <Имя> (строки X-Y[, экспорт]):` + текст; failure → `READ_PROCEDURE_FAILED: <КОД> — <текст>` (+ строка `Доступные процедуры: ...` при `available`).

**Режимы (REQ-022) поверх набора:** оба тула — read-операции, в `planning` и `review` остаются в surface без изменений; `apply_oneC_edit` гейтится как раньше (в `review` — sandbox-only, режим адресации `procedure` внутри доступен).

### task_board_update (REQ-021, v0.75.0 — typed)

Доска задач под управлением AI: липкая панель над тредом чата, AI раскладывает нетривиальную задачу на шаги и ведёт их по статусам. Client-executed; **состояние доски живёт на клиенте** (в рамках диалога), серверного хранения нет. Только `swan_code_client` — в `lensa_universal` не входит.

**Tool — `task_board_update(tasks[], replace?)`**. Семантика — **upsert по `id`** (реализует клиент): новый `id` добавляет строку, существующий — обновляет; не упомянутые задачи не трогаются; порядок строк = порядок первого появления `id`.
- input: `{"tasks":[{"id":"1","name":"Прочитать модуль","status":"done"},{"id":"2","name":"Добавить проверку","status":"in_progress","externalId":"T-343434","description":"проверь дубли по ИНН"},{"id":"3","name":"Синтакс-контроль","status":"pending","notes":"после правки"}],"replace":false}`. `id` — string, стабилен в рамках диалога, назначает AI; `name` обязателен при первом появлении id; `description?` (REQ-023, v0.77.0) — суть шага, вторая строка на доске; сюда же пользователь руками вписывает уточнения для AI; при обновлении можно опустить — прежнее значение сохраняется (семантика как у `name`, реализует клиент); `status` (ось AI) ∈ `pending|in_progress|done|blocked|cancelled`; `externalId?` — номер во внешнем трекере пользователя; `replace:true` — очистить доску и заполнить заново.
- success: `{"applied":3,"board":[{"id":"1","name":"...","description":"...","status":"done","userDone":true}, ...]}` — `applied` = строк создано/обновлено этим вызовом; `board` — **вся доска после применения**, включая `userDone` — отметку, поставленную **пользователем руками** (галочка в UI). Две оси намеренно раздельны: `status` — что думает AI, `userDone` — что подтвердил человек; возврат всей доски даёт AI увидеть пользовательские отметки без отдельного тула чтения.
- failure: `{"error":"INVALID_INPUT","message":"..."}` (пустой `tasks[]`, неизвестный `status`, нет `name` у новой задачи).
- LLM-проекция сервера: `ДОСКА ЗАДАЧ (обновлено N):` + строки `<i>. [<статус по-русски>] <name> (внешний: <externalId>) — <notes> — подтверждено пользователем` (последний хвост ⟺ `userDone:true`) + при непустом `description` вторая строка `   описание: <description>`; failure → `TASK_BOARD_FAILED: <код> — <текст>`. Статусы в проекции — **текстовые** («ожидает»/«в работе»/«выполнено»/«заблокировано»/«отменено»), эмодзи сервер не эмитит.
- Отметка галочкой пользователем уходит **обычным сообщением чата** («Отметил выполненной задачу 1: …») — отдельного контракта нет.

### list_oneC_task_boards (REQ-029, v0.81.0 — typed)

Доски задач **других диалогов**: клиент хранит доски локально (`task-boards.json`) с привязкой «диалог → база 1С» (`dialog-bindings.json`) и может ответить на «что уже планировалось по этой базе» **сам, без сервера** — сервер тул только декларирует и разворачивает результат. Client-executed **read**-тул; только `swan_code_client`. Закрывает триаду экономики диалога (REQ-027/028/029): уйти в новый (дешёвый для кэша) диалог, не теряя план.

**Tool — `list_oneC_task_boards(scope?)`**:
- input: `{"scope":"base"}` — `"base"` (умолчание) — только диалоги по **текущей** базе 1С; `"all"` — по всем базам. `scope` — string без enum (конвенция: клиент валидирует, сервер не опережает клиентские возможности).
- success: `{"boards":[{"sessionId":"...","title":"...","updatedAt":"RFC3339","tasks":[{"id":"1","name":"...","status":"done","externalId":"T-42","userDone":true,"description":"..."}]}]}` — shape задач как в `task_board_update.board`, без `notes`; `externalId`/`description` опциональны.
- **Пустой результат — не ошибка:** `{"boards":[]}` → позитивная строка «по этой выборке ещё ничего не планировалось».
- failure: `{"error":"<КОД>","message":"..."}`.
- LLM-проекция сервера: `ДОСКИ ЗАДАЧ ДРУГИХ ДИАЛОГОВ (N):`, на каждую доску заголовок `Доска <i>: <title> [диалог <sessionId>], обновлена <updatedAt>` + строки задач в формате `task_board_update` (текстовый статус, `(внешний: ...)`, `— подтверждено пользователем` ⟺ `userDone`, `описание:` второй строкой); failure → `TASK_BOARDS_FAILED: <код> — <текст>`.
- **Режимы (REQ-022):** read-тул — остаётся в surface в `planning` / `review` без изменений (как процедурные тулы REQ-024).

## Tool surface for query_console_1c

Набор тулов, активный для сессий product=`query_console_1c`. Сервер заявляет AI **23 тула** через native tool_use (F-000002 + v0.22.0 extension + v0.25.0 buffer-семантика + v0.26.0 — 4 новых read-only тула; v0.44.0 — `find_account`; v0.48.0 — `get_account_subkonto`; v0.53.0 — `get_journal_documents`; v0.54.0 — server-executed `get_latest_query_version`; v0.61.0 — server-executed `load_skill`, F-000033; v0.70.0 — server-executed `recall`, R2 T-000211). Результаты client-executed тулов возвращает клиент 1С через generic-форму `POST /v1/api/chat/tool-results` с полями `name` + `dataB64`; server-executed тул (Группа 6) исполняет сам сервер, в DTO ответа он не попадает.

Пять групп: метаданные конфигурации (drill-down), read-операции над текущим запросом, валидация, buffer-write правки, финальный коммит.

**v0.25.1 — buffer-семантика.** Правки `replace_table` / `replace_query_fragment` / `insert_table` / `delete_table` модифицируют **внутренний буфер** на 1С-стороне, в редактор пользователя НЕ пишут. Финальный коммит — `apply_query_change` без аргумента (применяет накопленный буфер) либо с `queryText` (пишет напрямую — для запроса с нуля).

### Группа 1: метаданные конфигурации (F-000002, v0.16.0)

**Tool 1 — `list_metadata_types`** (без параметров):
- raw от 1С (`dataB64` payload) — счётчики типов метаданных + опциональный env-блок конфигурации:
  ```json
  {
    "Справочники": 320, "Документы": 248, "Перечисления": 720, "РегистрыСведений": 154, "РегистрыНакопления": 38, "ПланыВидовХарактеристик": 12,
    "env": {
      "platformVersion": "8.3.24",
      "configurationName": "УправлениеТорговлей",
      "configurationSynonym": "Управление торговлей",
      "configurationVersion": "11.5.1.234"
    }
  }
  ```
- `env` (v0.29.1, T-000111) — опциональный вложенный объект; все 4 поля строки и опциональны (парсер не требует). Доносится до AI первым блоком («Окружение конфигурации»), т.к. `platformVersion` влияет на синтаксис запросов 1QL. Не-числовые ключи верхнего уровня (кроме `env`) молча пропускаются, а не валят тул.

**Tool 2 — `list_objects(type, namePattern?)`** (drill-down по одному типу):
- input от AI:
  ```json
  {"type": "Справочники", "namePattern": "Контр"}
  ```
- допустимые `type` (16, REQ-025): `Справочники`, `Документы`, `Перечисления`, `РегистрыСведений`, `РегистрыНакопления`, `ПланыВидовХарактеристик`, `Константы`, `ПланыСчетов`, `ПланыВидовРасчета`, `БизнесПроцессы`, `Задачи`, `РегистрыБухгалтерии`, `РегистрыРасчета`, `ЖурналыДокументов`, `Отчеты`, `Обработки`.
- raw от 1С — `{"<тип>": [{"n": "...", "s": "опц. синоним", "values": [...] (только для Перечислений)}, ...]}`.

**Tool 3 — `get_object_metadata(object)`** (структура конкретного объекта):
- input от AI:
  ```json
  {"object": "Справочник.Контрагенты"}
  ```
- raw от 1С — объект с полями `kind`, `n` и набором per-kind полей: `hierarchical`, `standardAttributes`, `attributes`, `tabularSections` (Справочник/Документ/ПВХ); `registerType`, `dimensions`, `resources` (РегистрНакопления); `periodicity`, `leadingDimensions` (РегистрСведений); `values` (Перечисление); `valueTypes` (ПВХ); `periodicity`, `hasActionPeriod`, `hasBasePeriod`, `schedule`, `recalculations` + `dimensions`/`resources`/`attributes` (РегистрРасчета, см. ниже).
- **РегистрРасчета (v0.60.0, T-000147):** помимо `dimensions`/`resources`/`attributes` (как у прочих регистров) несёт специфику расчёта: `periodicity` (string), `hasActionPeriod` (bool — есть период действия → виртуальная таблица `ФактическийПериодДействия`), `hasBasePeriod` (bool → `База<БазовыйРегистр>`), `schedule` (`{chart, value?, date?}` — связь с регистром-графиком → `ДанныеГрафика`; опускается, если графика нет), `recalculations` — массив `[{n, dimensions[]}]` (перерасчёты: имя + измерения пересчёта) ИЛИ **свёрнутый флаг** `{t:1,c:N}` при числе перерасчётов выше клиентского порога. Сервер рендерит заголовок с периодичностью, строки «Период действия / Базовый период: есть» (только при `true`), «График: `<chart>`», блоки Измерения/Ресурсы/Реквизиты и «Перерасчёты». Детальная семантика запросов (две оси времени, вытеснение, база) — в L3-промпте консоли, в рендере метаданных не дублируется.
- **Бухгалтерская проекция (v0.37.0):** `ПланСчетов` — `subkontoTypes {characteristicType, maxCount, items}` (виды субконто из связанного ПВХ), `accountingFlags` (признаки учёта), `subkontoAccountingFlags` (признаки учёта субконто). `РегистрБухгалтерии` — `chartOfAccounts` (ссылка на план счетов), `correspondence` (bool, поддержка Дт/Кт), `subkonto {maxCount, characteristicType}`. Сервер рендерит динамические слоты движений: при `correspondence` → `СубконтоДт1..N / СубконтоКт1..N`, иначе `Субконто1..N` (при N=1 диапазон схлопывается; при N=0/отсутствии — строка опускается). `subkontoTypes.items` может прийти **свёрнутым** `{t:1,c:N}` (виды субконто бывают десятками) → сервер рендерит `всего N (свёрнуто) — смотри get_object_metadata(ПланВидовХарактеристик.<X>)`.
- **Предопределённые элементы (v0.37.0, ключи cd/s с v0.44.0):** поле `predefined` — массив `[{n, s?, cd?}]` ИЛИ **свёрнутый флаг** `{t:1,c:N}` (план счетов = сотни счетов; v0.44.0). Generic-рендер, единый для трёх kind'ов. **Справочник и ПланВидовХарактеристик** (кода нет): `{n, s}` (`n` = имя предопределённого для запроса, `s` = наименование) → `- Имя — Наименование` (напр. `- ВсеОбъектыАдресации — Все объекты адресации`); при буквальном `s==n` дедуп → `- Имя` (напр. `Аттестат`). **ПланСчетов** (есть код): `{n, cd, s}` (`cd` = код счёта, был `code` — принимаются ОБА; `s` = наименование) → `- 26 — Общехозяйственные затраты (ОбщехозяйственныеЗатраты)` (код-first; наименование `s` и имя `n` оба — `s` читаемо, `n` для ссылок `ПланСчетов.X.n`; дедуп при `s==n`). Свёрнутый `predefined`: у плана счетов → `всего N (свёрнуто) — уточни find_account(<план>, namePattern)`; у справочника/ПВХ → `всего N (свёрнуто)` (точечного тула раскрытия нет). Порядок — как пришёл (сервер не сортирует).
- Поле `type` реквизита/измерения/ресурса принимается в **формах** (сервер толерантен ко всем): строка (`"Справочник.Контрагенты"`, query-форма; legacy `СправочникСсылка.*` тоже принимается), массив строк (`["Справочник.Физлица","Справочник.Контрагенты"]`), единый флаг свёртки `{t:1,c:N}` (с v0.44.0) либо legacy `{composite:true,typeCount:N}` (оба принимаются) для составных мультитиповых полей. Та же толерантность — у типов параметров в `get_query_parameters`. Порог схлопывания задаёт сервер: в `input` тула инъецируется `compositeTypeThreshold` (см. `console1c.composite_type_threshold` в `PUT /v1/admin/config`). Рендер: `составной тип (N вариантов)`. Раскрытие составного типа по требованию — `get_attribute_type_details` (Tool 3a).
- При наличии хотя бы одного составного поля в ответ добавляется footer-подсказка про `get_attribute_type_details` (v0.39.0).

**Tool 3a — `get_attribute_type_details(object, attributeName, tabularSection?, namePattern?)`** (раскрытие составного типа, v0.39.0):
- input от AI: `object` (`<Тип>.<Имя>`), `attributeName` (имя реквизита/измерения/ресурса), `tabularSection?` (если реквизит в табчасти), `namePattern?` (подстрока имени типа, case-insensitive; обязателен при > 50 типов).
- raw от 1С:
  ```json
  {"object":"Справочник.Контрагенты","attributeName":"Владелец","tabularSection":"",
   "totalTypes":3,"matchedTypes":3,
   "types":["Справочник.Организации","Справочник.ФизическиеЛица","Перечисление.ТипыКонтрагентов"]}
  ```
- Имена типов — в **запросной форме** (`Справочник.X`, `Перечисление.X`), точные (резолв через `ПолноеИмя`, не синоним). Фильтрация по `namePattern`, guard и резолв — на стороне клиента.
- **Guard от вывала:** если `namePattern` не задан и `totalTypes > 50` — поле `types` **не присылается**, вместо него `note` с требованием уточнить (боевой пример: 2292 типа). Составные ≤ 50 без паттерна отдаются целиком.
- LLM-проекция: при `types` — список с `всего N, показано M`; при `note` — счётчик + текст note; при пустом матче — «по фильтру ничего не найдено».
- **Поле субконто движений (REQ-019, v0.53.0):** если AI обратится к синтетическому полю субконто регистра бухгалтерии (`СубконтоДт{N}` / `СубконтоКт{N}` / `Субконто{N}`), клиент возвращает **штатное перенаправление** `{"kind":"subkontoFieldRef","object":...,"attributeName":"СубконтоДт1","subkontoField":true,"account":"1"}` (не ошибку). Сервер рендерит инструкцию-перенаправление на `get_account_subkonto` (тип субконто зависит от счёта, а не фиксирован на уровне регистра), подставляя номер из `account` в `Субконто{N}` / `СубконтоДт{N}`/`СубконтоКт{N}`. Это бэкстоп — основной путь (типы субконто через `get_account_subkonto`) закрыт промптом.

**Tool 3c — `get_account_subkonto(chartName, accountCode, namePattern?)`** (виды субконто конкретного счёта, REQ-017, v0.48.0):
- input от AI: `chartName` (имя плана счетов), `accountCode` (код счёта, напр. `01`, `60.01`; при неизвестном точном коде — сначала `find_account`), `namePattern?` (подстрока имени типа значения субконто — раскрывает составной тип; без него типы свёрнуты при > 5).
- raw от 1С:
  ```json
  {"chartName":"Хозрасчетный","account":{"cd":"01","s":"Основные средства"},"maxCount":3,
   "subkonto":[{"pos":1,"n":"ОсновныеСредства","s":"Основные средства","type":["Справочник.ОсновныеСредства"],"sum":true}]}
  ```
  где `subkonto[{pos,n,s,type,sum?,cur?,qty?,turnoverOnly?}]` — виды субконто счёта по порядку `pos`; `type` — list типов либо свёртка `{t:1,c:N}`; признаки учёта присутствуют только когда `true`. При ошибке — `{"error":"<текст>"}` (план/счёт не найден).
- LLM-проекция: заголовок `Субконто счёта 01 «Основные средства» (план Хозрасчетный, макс. субконто 3):` + строки `- Субконто{pos}: {s} — тип ... [флаги]` + форма отбора `Значение(...)` + slots-hint (`Субконто{N}` в Остатках; `СубконтоДт{N}/СубконтоКт{N}` в движениях). Фильтр/guard/резолв — на клиенте. Серверный backstop длины списка типов — `console1c.collection_render_cap` (T-000188).

**Tool 3d — `get_journal_documents(journalName, namePattern?)`** (состав документов журнала, REQ-020, v0.53.0):
- input от AI: `journalName` (имя журнала документов как в конфигурации, узнаётся через `list_objects(ЖурналыДокументов)`), `namePattern?` (подстрока имени документа; без паттерна — весь состав).
- Зачем: `get_object_metadata` журнала отдаёт состав `registeredDocuments` **свёрнутым** `{t,c}` при большом числе документов (клиентский порог 20) — `get_journal_documents` раскрывает по подстроке (в семье с `get_attribute_type_details` и `get_account_subkonto`).
- raw от 1С:
  ```json
  {"kind":"journalDocuments","journalName":"ПерсонифицированныйУчет","totalDocuments":42,
   "documents":["ПачкаДокументовАДВ_2","ПачкаДокументовАДВ_1"]}
  ```
  При ошибке — `{"error":"<текст>"}` (журнал не найден / нет параметра).
- LLM-проекция: `Документы журнала X (всего N):` + список `- ИмяДокумента`. Пустой `documents` при `totalDocuments > 0` → «по фильтру ничего не найдено — уточни namePattern»; при `totalDocuments = 0` → «журнал не содержит документов». Фильтр/резолв состава — на клиенте.

**Tool 3b — `find_account(chartName, namePattern?)`** (точечный поиск счёта в плане счетов, v0.44.0):
- input от AI: `chartName` (имя плана счетов, обязателен), `namePattern?` (подстрока по коду/имени/наименованию, case-insensitive; обязателен при > 50 счетах).
- raw от 1С:
  ```json
  {"chartName":"Хозрасчетный","c":489,"matched":2,
   "accounts":[
     {"n":"ОбщехозяйственныеЗатраты","cd":"26","s":"Общехозяйственные затраты"},
     {"n":"Касса","cd":"50","s":"Касса"}
   ]}
  ```
  где `c` — всего счетов в плане, `matched` — сколько нашлось, `accounts[{n,cd,s}]` — имя предопределённого / код / наименование.
- **Guard от вывала:** при `matched:0` + `t:1` (без `accounts`) — счетов слишком много без `namePattern`. Сервер рендерит `всего N (свёрнуто). Уточни: find_account(<план>, namePattern).`
- LLM-проекция: при `accounts` — `Счета плана X — всего N, найдено M:` + строки `- cd — s (n)` (дедуп при буквальном `s==n`); иначе — инструкция уточнить. Фильтр/guard/резолв кода (через `СокрЛП`) — на стороне клиента; сервер декларирует тул и рендерит.

### Группа 2: read-операции над текущим запросом (v0.22.0)

Read-only тулы для контекста AI о уже существующем запросе пользователя — обязательны перед точечной правкой через replace_*.

**Tool 4 — `get_current_query_text`** (без параметров, v0.22.0):
- raw от 1С:
  ```json
  {"queryText": "ВЫБРАТЬ ... ИЗ Справочник.Контрагенты ГДЕ ..."}
  ```
- Пустой `queryText` или отсутствующий — редактор пуст; LLM-проекция: `(редактор пуст — текущего запроса нет)`.
- **Порог строк (v0.36.0, T-000110):** сервер считает строки `queryText`; если их **больше** `console1c.query_text_line_threshold` (глобальный server-config, дефолт 50, hot-reload) — вместо полного текста LLM-проекция: `QUERY_TOO_LARGE: запрос большой (N строк). Полный текст не выдаётся. Используй get_query_tables (список пакетов) и get_table_text(индекс) для нужных.` Блокировка server-side, **клиент не меняется** (как и раньше шлёт полный `queryText`). Ровно N строк — проходит; пустой редактор не блокируется. Порог ≤ 0 отключает проверку.

**Tool 5 — `get_query_tables`** (без параметров, v0.22.0; v0.26.0 — опц. метрики выполнения):
- raw от 1С — список пакетов запроса:
  ```json
  {
    "tables": [
      {"index": 0, "name": "#Добавление_ВТ_Контрагенты"},
      {"index": 1, "name": "#Уничтожение_ВТ_Контрагенты"},
      {"index": 2, "name": "#Запрос1", "executionTimeMs": 12, "rowCount": 42}
    ]
  }
  ```
- Имена пакетов: `#Добавление_<имя>` (ВЫБРАТЬ ... ПОМЕСТИТЬ), `#Уничтожение_<имя>` (УНИЧТОЖИТЬ), `#Запрос<N>` (финальный пакет без ПОМЕСТИТЬ).
- Поле `index` (0-based) передаётся в `tableIndex` для `replace_table` (`replace_query_fragment` с server-resolved диффом `tableIndex` не использует — см. tool 11).
- **v0.26.0:** опц. `executionTimeMs` (int, мс) и `rowCount` (int) на пакете — присутствуют только если пакет выполнялся (иначе полей нет, не `null`).
- **v0.30.0 (T-000118):** парсинг метрик толерантен к типу — принимается и число (`12`), и строка (`"12"`); `null` / пусто / нечисловой мусор → метрика просто опускается, тул не падает. Раньше метрика-строкой валила весь `get_query_tables` с `INVALID_TOOL_DATA`.

**Tool 6 — `get_query_parameters`** (без параметров, v0.22.0):
- raw от 1С — список параметров с типами:
  ```json
  {
    "parameters": [
      {"name": "НачалоПериода", "type": "Дата", "isList": false, "filled": true},
      {"name": "Контрагенты", "type": "Справочник.Контрагенты", "isList": true, "filled": false}
    ]
  }
  ```
- Поля: `name` (без `&`), `type` (сериализованный XDTO XML — сервер показывает as-is, AI поймёт), `isList` (для конструкции `В (&Параметр)`), `filled` (заполнено ли значение).
- **Опциональное поле `value`** — присутствует только если пользователь явно разрешил отправку значений параметров в UI 1С (по умолчанию нет — приватные данные БД). Тип `value` зависит от `isList`: одиночное значение (любой JSON-тип) или массив. Серверная сторона показывает raw как есть.

### Группа 2b: расширенные read-only тулы (v0.26.0)

**Tool — `get_table_text(tableIndex)`**:
- input: `{"tableIndex": 1}`
- raw от 1С: `{"tableIndex": 1, "queryText": "ВЫБРАТЬ ..."}` либо `{"error": "..."}`
- Текст одного пакета по индексу из `get_query_tables` (после нормализации через схему).

**Tool — `get_table_field_count(tableIndex)`**:
- input: `{"tableIndex": 0}`
- raw от 1С:
  ```json
  {"tableIndex": 0, "fieldCount": 3, "fieldNames": ["Код", "Наименование", "Сумма"]}
  {"tableIndex": 2, "fieldCount": 0, "note": "Пакет не является запросом выборки (нет колонок)"}
  {"error": "..."}
  ```
- Число и имена колонок результата пакета. Выполнение НЕ требуется — колонки из схемы.

**Tool — `get_query_metrics`** (без параметров):
- input: `{}`
- raw от 1С: `{"valid": true, "lineCount": 12, "charCount": 340, "tableCount": 3, "joinCount": 2}`. При `valid=false` — `lineCount`/`charCount` заполнены, `tableCount`/`joinCount`=0.
- Оценка масштаба текущего запроса. Деталь ошибки валидности — через `validate_query`.

**Tool — `get_console_settings`** (без параметров):
- input: `{}`
- raw от 1С: `{"settings": [{"name": "ВыводитьРезультат", "label": "Выводить результат", "value": true}, ...]}` — 9 настроек (`name`/`label`/`value`; `value` любого JSON-типа: bool/string/int).

### Группа 3: валидация (v0.25.1)

**Tool 7 — `validate_query`** (v0.26.0 — без параметров):
- input от AI:
  ```json
  {}
  ```
- raw от 1С:
  ```json
  {"valid": true}
  {"valid": false, "error": "unknown field", "line": 3, "column": 12}
  ```
- Read-only operation: client-side через 1С-парсер. **Параметр `tableIndex` убран в v0.26.0** — изолированная валидация одного пакета невозможна (ВТ ссылается на предыдущие), валидируется весь запрос. Полезна между правками буфера до `apply_query_change()`.

### Группа 4: buffer-write правки (v0.22.0 + v0.25.1 buffer-семантика)

Модифицируют **внутренний буфер на 1С-стороне**, в редактор НЕ пишут. Возвращают только статус операции. Финал — `apply_query_change()` без аргумента (коммит буфера).

**Tool 8 — `replace_table(tableIndex, newText)`** (v0.22.0, buffer-семантика v0.25.1):
- input от AI:
  ```json
  {
    "tableIndex": 1,
    "newText": "ВЫБРАТЬ Контрагенты.Ссылка ПОМЕСТИТЬ ВТ_Контрагенты ИЗ Справочник.Контрагенты ГДЕ Контрагенты.Недействителен = ИСТИНА"
  }
  ```
- raw от 1С:
  ```json
  {"replaced": true}
  {"replaced": false, "error": "Синтаксическая ошибка ...", "line": 3, "column": 12}
  ```

**Tool 9 — `insert_table(beforeIndex, newText)`** (v0.25.1):
- input от AI:
  ```json
  {"beforeIndex": 1, "newText": "ВЫБРАТЬ * ПОМЕСТИТЬ ВТ_X ИЗ Справочник.X"}
  ```
- raw от 1С:
  ```json
  {"inserted": true, "newTableIndex": 1}
  {"inserted": false, "error": "beforeIndex out of range", "line": 2, "column": 5}
  ```
- Новый пакет встаёт ПЕРЕД `beforeIndex`. Если `beforeIndex >= количества пакетов` — клиент вставляет в конец. Поле фактической позиции — `newTableIndex` (v0.26.0; ранее ошибочно документировалось как `insertedAt`). `newText` может содержать несколько пакетов через `;`. После insert индексы существующих пакетов сдвигаются — AI должен перечитать через `get_query_tables`.

**Tool 10 — `delete_table(tableIndex | tableIndexes[])`** (v0.25.1; v0.26.0 — мульти):
- input от AI (одиночное или мульти):
  ```json
  {"tableIndex": 0}
  {"tableIndexes": [0, 2]}
  ```
- raw от 1С:
  ```json
  {"deleted": true}
  {"deleted": false, "error": "tableIndex out of range", "line": 1, "column": 1}
  ```
- v0.26.0: можно удалить несколько пакетов за вызов через `tableIndexes[]` (сдвиг индексов учитывает клиент). Response упрощён до `{deleted, error?, line?, column?}` — индексы не возвращаются. Индексы оставшихся пакетов сдвигаются — AI должен перечитать через `get_query_tables`.

**Tool 11 — `replace_query_fragment(edits[])`** (server-resolved дифф, F-000018; **implemented — серверная машинерия живая с v0.56.0** (T-000226..228: `ApplyEdits` + server-resolved loop во всех путях); клиентский исполнитель приёма `{fullText}` в буфер — в работе у консоли (их T-000016), до его отгрузки тул гейтится пометкой `[пока недоступен]` в live-промпте):
- **Паттерн: server-resolved, client-applied.** Дифф считает **сервер** (1С дифф-логику не реализует); 1С получает готовый полный текст и кладёт его в буфер (whole-query replace). `tableIndex` НЕ используется — дифф по всему тексту канон-головы.
- input **от AI** (модель → сервер) — мелкие правки:
  ```json
  {
    "edits": [
      {"action": "replace", "oldText": "Контрагенты.Наименование", "newText": "Контрагенты.Наименование, Контрагенты.ИНН"},
      {"action": "insert_after", "anchor": "ГДЕ Контрагенты.Недействителен = ЛОЖЬ", "newText": "\n    И Контрагенты.ИНН <> \"\""}
    ]
  }
  ```
  `replace` — `oldText` должен быть **уникален во всём запросе** (иначе ошибка); `insert_after` — вставка после первого вхождения уникального `anchor`. Edits применяются строго последовательно.
- input **к 1С** (сервер → клиент, дифф уже применён сервером) — готовый полный текст:
  ```json
  {"fullText": "<полный новый текст запроса 1QL>"}
  ```
  1С кладёт `fullText` в буфер изменений **целиком** (как `replace_table`, но на весь запрос), в редактор НЕ пишет. `edits`/`tableIndex` клиенту не приходят.
- raw **от 1С** — формат как у tool 8: `{"replaced": true}` / `{"replaced": false, "error": "...", "line": ?, "column": ?}`.
- Ошибки диффа (`oldText` не найден / не уникален) резолвятся **на сервере инлайн** — round-trip в 1С не происходит; модель получает `REPLACE_FAILED` и чинит дифф.
- Финал — `apply_query_change()` без аргумента (коммит буфера). См. ANNOUNCE-002 + спеку `docs/specs/2026-06-29-replace-query-fragment-server-resolved.md`.

### Группа 5: финальный коммит (v0.25.1 расширен optional queryText)

**Tool 12 — `apply_query_change(queryText?)`** (F-000002 + v0.25.1 buffer-семантика):
- input от AI варианты:
  ```json
  {"queryText": "ВЫБРАТЬ ... ИЗ Справочник.Контрагенты ГДЕ ..."}
  ```
  (применить переданный текст напрямую — сценарий A или полная переписка)
  ```json
  {}
  ```
  (коммитить накопленный буфер от replace_*/insert_table/delete_table)
- **Перехват пустого apply (T-000251, v0.62.1):** если `queryText` пустой (`""`) ИЛИ отсутствует, **И** в текущем ходе (после последнего user-сообщения) не было buffer-тулов (`replace_table`/`insert_table`/`delete_table`/`replace_query_fragment` — все складывают изменения в буфер, коммит которого и есть пустой apply) — применять нечего. Сервер НЕ шлёт клиенту пустой apply, а подставляет **канон-голову** (последняя серверная версия запроса, актуальная ПОСЛЕ тулов хода), переписывая input к 1С на:
  ```json
  {"queryText": "<полный текст канон-головы>", "fromServer": true}
  ```
  Флаг `fromServer: true` — маркер, что текст **серверный** (восстановление канона), а не правка модели: клиент применяет его в редактор молча, не как agent-suggestion. `tool_use.id` сохраняется; оригинальный (пустой) вызов остаётся в истории провайдера (переписка живёт только в транспорте к клиенту, как у `replace_query_fragment`). Есть buffer-тулы в ходе → apply без `queryText` = легитимный commit буфера, сервер НЕ перехватывает. Канон пуст → сервер возвращает модели инлайн-подсказку (`APPLY_NOOP`) написать `queryText`, round-trip к 1С не происходит. Хэш подставленного текста == канон-голова → echo-dedup не пишет фальшивую server-версию. См. ANNOUNCE-006.
- **Дискриминация источника apply на клиенте (REQ-042).** Клиент выбирает подпись прогресс-строки по двум сигналам relay-нагрузки live-хода — `fromServer` и наличие `queryText` — новое поле не требуется:

  | Ситуация | `queryText` в input | `fromServer` | Подпись клиента |
  |---|---|---|---|
  | Модель прислала новый текст запроса | есть | отсутствует | «Применяю новый текст запроса» |
  | Сервер восстановил канон (пустой apply, без буфера) | есть | `true` | «Сохраняю/восстанавливаю запрос» (серверный текст, не правка модели) |
  | Коммит накопленного буфера (`replace_table`/`replace_query_fragment`/…) | нет | отсутствует | «Сохраняю накопленные изменения» |

  Т.е. `fromServer:true` ⇔ «источник = сервер/накопленное» (эквивалент запрошенного `applySource:"edits"`), а `queryText` без `fromServer` ⇔ «новый текст модели».
- raw от 1С:
  ```json
  {"applied": true, "committedFromBuffer": false}   // queryText применён
  {"applied": true, "committedFromBuffer": true}    // буфер закоммичен
  {"applied": true, "error": "Поле не найдено"}      // v0.61.1: применён, но НЕ прошёл валидацию — текст В редакторе; LLM видит APPLY_OK_INVALID и чинит; server-версия accepted=true
  {"applied": true, "skdValid": false, "skdError": "..."} // v0.61.1: применён, но не валиден для СКД — приписка в LLM-проекцию
  {"applied": false, "error": "буфер пуст"}          // apply() без queryText, буфера нет
  {"applied": false, "error": "..."}                 // ошибка валидации/применения (не применён)
  ```
- Особенность: на success 1С обычно НЕ шлёт `/chat/tool-results` (диалог закрыт локально). На failure — шлёт, и AI получает шанс исправить. Retry-лимит общий (`ai_tool_retry.max_attempts`).
- **Двухосевая модель (v0.61.1, T-000245):** `applied` — ФАКТ «текст лёг в редактор» (управляет accepted server-версии), `error`/`skdValid`/`skdError` — свойства самого текста. Кейс «применился, но невалиден» легален: applied=true + error≠"".

### Server-side unpacker

Сервер разворачивает `dataB64` payload в plain-text LLM-проекцию (`tool_result.content`) через имплементацию в `internal/domain/tools/console1c/`. Клиент не получает сырого JSON в ответе AI — он видит только следующий `tool_use` (если AI запрашивает ещё drill-down / read / правку) или финальный текст.

**Buffer-flow.** AI делает несколько правок подряд (любая последовательность replace_table / insert_table / delete_table), каждая возвращает только статус. После правок — `apply_query_change()` без аргумента, и буфер коммитится в редактор. Альтернатива — `apply_query_change(queryText)` с полным текстом для сценария с нуля.

### Группа 6: server-executed тулы (C3, v0.54.0)

**Tool 13 — `get_latest_query_version`** (без параметров, F-000018):
- **Server-executed.** В отличие от всех тулов выше (client-executed — их результат шлёт 1С через `POST /v1/api/chat/tool-results`), этот тул **исполняет сам сервер** против серверной истории версий запроса (`query_versions_*`), без round-trip в 1С-рантайм. Объявляется AI наравне с остальными, но **в клиентский DTO ответа НЕ попадает** — клиент его никогда не видит и не обрабатывает.
- Возврат (server-side рендер в `tool_result.content`): текущая (последняя) версия текста запроса — канон-голова: сам текст + происхождение (`правка человека` / `правка агента`) + для агентских правок статус применения (`применена` / `отклонена` / `ожидает`). Версий нет → `LATEST_QUERY_VERSION: история запроса пуста`.
- v1 отдаёт только текущую версию; история/диффы — развитие в следующих версиях.
- **Показывается на живом ходе (REQ-049, `v0.66.1`):** факт вызова доносится клиенту в `serverToolProgress` (см. `client-api-chat.md`) как non-actionable маркер — клиент рисует «Сверяю сохранённую версию запроса» (наравне с `load_skill`). На reload он уже присутствует в `toolCalls` истории. Delta-per-response (REQ-050b) гасит повтор на многосегментном ходе.
- **Server-tool loop.** Ответ AI с server-tool сервер исполняет инлайн и **пере-вызывает AI** (sync — в том же запросе; async — пере-сабмит тикета в pending, клиент продолжает поллить), пока ответы содержат только server-tools. Mixed-ответ (server + client tools) → server исполнен инлайн, client-tools бунсятся клиенту как обычно. Кап итераций — `console1c.server_tool_max_iters` (дефолт 3). Каждая итерация — отдельный reserve/commit биллинга по залоченному на ход курсу. Клиентский контракт (`/chat/tool-results` с client-tools) **не меняется** — server-tools для 1С прозрачны.

**Tool 22 — `load_skill`** (`{name}`, F-000033, v0.61.0):
- **Server-executed** (как tool 13, клиент его не видит). Подгружает доменный knowledge-пак (skill) по требованию модели: тело сервер возвращает в `tool_result` из таблицы `skills` — оно оседает в истории диалога. Стартовые паки консоли: `buh_skill` / `zup_skill` / `trade_skill`.
- **Каталог `{name, description}` — per-turn, минус загруженные (REQ-050a, `v0.66.1`).** Каталог активных скиллов инжектится **в хвост контекста каждый ход** (не в замороженный system_prompt) и **исключает уже загруженные в этом диалоге** навыки — их тело уже в истории. Модель не видит загруженный навык в списке «доступных к загрузке» → **не переэмитит `load_skill` каждый ход** (устраняет UX-шум дублирующихся чипов, лишние reserve/commit и растрату `console1c.server_tool_max_iters`). Это показываемый server-tool (`serverToolProgress`), поэтому чип виден один раз.
- Неизвестное/неактивное имя → inline error-`tool_result` `SKILL_NOT_FOUND: '<имя>'. Доступные: ...` — модель самокорректируется, ход не рвётся.
- Управление паками — `/v1/admin/skills` (см. Admin: Skills).

**Tool 23 — `recall`** (`{name}`, R2 v0.70.0, T-000211):
- **Server-executed** (как tool 13/22, клиент его не видит). Читает **durable-ссылку компактизации** по имени из `chat_session_references` — body ссылки (точный текст сохранённого факта) сервер возвращает в `tool_result`. R1 (v0.68.0) пишет references при компактизации (auto-trigger или `POST /compact`); R2 даёт модели их читать.
- **НЕ показывается на живом ходе** (D4 спеки R2 — read-операция, не доменно-значимое действие). `tool_result` появляется в истории чата (видно в admin-messages), но `serverToolProgress` маркер НЕ эмитится.
- **Список доступных имён** — модель узнаёт из синтетической `role=checkpoint`-строки в `messages` (R1 T-000268 — показывает что контекст был сжат) или из `tool_result` предыдущей компактизации.
- Не найдена → inline error-`tool_result` `RECALL_NOT_FOUND: '<имя>' не найден. Доступные: ...` — модель самокорректируется, ход не рвётся (аналог `SKILL_NOT_FOUND`).
- Case-insensitive match по `lower(name)` (matching R1 dedup `ON CONFLICT (session_id, lower(name))`).
- Компактизация отключена (`compactionRepo == nil`, dev / R1 не поднят) → `RECALL_NOT_FOUND` без panic.
- **Product scope:** `query_console_1c` + `default`. НЕ `lensa_universal` (бестуловый продукт, D3 спеки R2).
- Admin-инспекция ссылок — `GET /v1/admin/sessions/{id}/references` (R2, T-000280).

### Формат `n` / `s` (metadata)

В raw-payload 1С присылает имя в `n` и опционально синоним в `s`. Синоним добавляется в LLM-проекцию строкой `- Имя — Синоним` только если он отличается от auto-формы (capitalize + space-split по PascalCase). Это снижает дублирование без потери информации (см. spec F-000002 §8).

---


## Поддержка инструментов

С v0.33.0 tool_use идёт единым путём через Swan_AiRouter (wire-формат
`tools=[{name, description, input_schema}]` / `tool_calls=[{id, name, input}]`);
per-provider адаптация — на стороне Router'а (для GigaChat Router сам стрипает
tool-роли — такие модели отвечают plain-текстом без tool-цикла). Текстовый
SEARCH/REPLACE-fallback удалён вместе с direct-GigaChat.

Клиент не видит разницы — ответы приходят в едином формате `codeChanges` /
`executeCommands` / `toolUses` (base64).
