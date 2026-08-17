# Client API — Chat (chat / async / result / stream)

> Часть client-контракта. Индекс: [client-api.md](client-api.md). Продолжение хода (tool-цикл) — [client-api-chat-toolresults.md](client-api-chat-toolresults.md).

# Public API — Chat (требует `swan_uk_*` + `X-Product-Key: swan_pk_*`)

## POST /v1/api/chat

v0.50.0 chat correctness notes:
- `temperature` is tri-state: omitted or `null` inherits session/default; explicit `0.0` is valid; values outside `[0.0, 2.0]` return `400 INVALID_TEMPERATURE`.
- On existing sessions, requested `model`/`temperature` are per-turn until the AI response succeeds. `sessions.model/temperature` are last-success state, not last-attempt state.
- Assistant messages store per-message attribution (`model`, `temperature`, optional `realModel`).

Отправка сообщения AI-провайдеру. Единый вход для всех провайдеров.

**Заголовки:** обязательны оба — `Authorization: Bearer swan_uk_*` (identity пользователя для биллинга) и `X-Product-Key: swan_pk_*` (identity приложения для аналитики и tool-set'а).

**Биллинг (с v0.41.0 — внешний Swan_Billing, F-000012):** перед вызовом провайдера сервер резервирует worst-case стоимость (Hold) в Swan_Billing. После ответа списывается реальная стоимость (settle через durable-outbox), остаток возвращается. При недостатке средств — `402 INSUFFICIENT_BALANCE`; при недоступном/несконфигуренном Billing — `503 BILLING_NOT_CONFIGURED` (hard-dependency, AI не вызывается). Стоимость в коинах считает сервер (`model_prices` + курс); Billing — чистый реестр коинов.

**Balance-aware max_tokens (F-000020, `v0.72.0`):** если остатка баланса не хватает на максимальную длину ответа (8192 токенов, при thinking 16384), сервер урезает `max_tokens` до оплачиваемого остатка — worst-case резерв считается по урезанному бюджету, так что ход проходит вместо ложного `402` («хватает на ответ, но не на теоретический максимум»). Длинный ответ при низком балансе может быть обрезан провайдером (thinking-бюджет внутри капа). Если остатка не хватает даже на минимум (512 output-токенов) — `402 INSUFFICIENT_BALANCE` **до** вызова AI (input-стоимость не сжигается). Применяется ко всему семейству `/v1/api/chat*` (`/stream`, `/async`, `/tool-results`, `/tool-retry`).

**Prompt caching (T-000286, `v0.73.0`):** сервер прозрачно кэширует стабильный префикс контекста (system + история до текущего хода) на стороне AI-провайдера (`cache_control`-закладка через Router, REQ-013). Для клиента ничего не меняется в запросе; эффект — **дешевле input** на длинных диалогах и tool-loop'ах: cache-read токены биллятся по сниженной ставке (~×0.10 input-цены), первая запись префикса — чуть дороже (~×1.25). Работает на моделях с поддержкой кэша (Anthropic и др.); прочие — без изменений. Наблюдаемость: `cachedTokens` в `GET /v1/api/sessions/{id}/summary` (тоталы + `byModel[]`). Применяется ко всему семейству `/v1/api/chat*`.

**Ограничение моделей (F-000015):** если у продукта настроен allow-list моделей и запрошенная `model` в него не входит — `403 MODEL_NOT_ALLOWED` (проверка ДО резерва баланса и вызова провайдера). Сбой проверки allow-list — `500 MODEL_ACCESS_CHECK_FAILED` (fail-closed, не pass-through). Применяется ко всему семейству `/v1/api/chat*` (`/stream`, `/tool-results`, `/tool-retry`). Пустой allow-list продукта = все модели разрешены. Управление списком — `GET`/`PUT /v1/admin/products/{id}/models`.

**Входные лимиты (T-000338, `v0.85.0`):** тело любой JSON-ручки API ограничено **256 KB** (превышение — `413 REQUEST_TOO_LARGE`); для tool-results семейства (`/chat/tool-results[/async]`, `/chat/tool-retry`) кап поднят до **4 MB** (результаты клиентских тулов легитимно крупные). Поле `message` дополнительно ограничено по длине — по умолчанию **64 000 символов** (админ-настройка, hot-reload); превышение — `400 MESSAGE_TOO_LONG` (в `message` ответа — лимит и фактическая длина). Сырые листинги/файлы целиком в `message` отправлять не нужно — для работы с текстом есть точечные тулы. Рекомендация клиенту: капнуть поле ввода в UI и показывать человекочитаемую ошибку. Применяется ко всему семейству `/v1/api/chat*` (`/chat`, `/async`, `/stream`).

**Request:**
```json
{
  "sessionId": "",
  "provider": "LenSA",
  "model": "anthropic/claude-sonnet-4.6",
  "message": "Проанализируй код проекта",
  "promptCode": "project_analysis",
  "framework": "1С",
  "ide": "Конфигуратор 1С",
  "version": "8.5.1",
  "projectText": "// код проекта...",
  "temperature": 0.7,
  "queryText": "ВЫБРАТЬ 1 ИЗ Справочник.Товары"
}
```

| Поле | Тип | Обязательно | Описание |
|------|-----|-------------|----------|
| sessionId | string | нет | UUID сессии. Пустая строка — автосоздание |
| provider | string | **нет** | **Deprecated** — понятие провайдера убрано из контракта (T-000329): значение игнорируется, поле можно не присылать. Принимается для обратной совместимости. (Ранее пустая строка → `400 PROVIDER_REQUIRED`; этот код снят.) |
| model | string | да | ID модели, канонический формат `vendor/name` (legacy-ID без `/` старых сессий резолвятся автоматически) |
| message | string | да | Текст сообщения пользователя |
| promptCode | string | нет | Код системного промпта из БД (`project_analysis`, `error_explain`, и т.д.) |
| framework | string | нет | Фреймворк проекта (для подстановки в промпт) |
| ide | string | нет | IDE пользователя |
| version | string | нет | Версия фреймворка/IDE |
| projectText | string | нет | Код/текст проекта для анализа |
| temperature | float | нет | Температура генерации (0.0-2.0, по умолчанию 0.7) |
| thinking | bool | нет | Активировать расширенное мышление (Claude: extended_thinking, OpenAI: reasoning_effort=high). По умолчанию false. При thinking сервер не передаёт temperature провайдеру |
| reasoningEffort | string | нет | Уровень размышлений (REQ-007): `none\|minimal\|low\|medium\|high`. Аддитивно к `thinking` — при заданном `reasoningEffort` он приоритетнее (шлётся Router'у как `reasoning_effort`); пусто → back-compat по `thinking`. Router мягко клэмпит на не-reasoning моделях (эхо реального уровня — в ответе). Невалидное значение → `400 INVALID_REASONING_EFFORT` |
| asyncWaitMs | int | нет | Только для `POST /v1/api/chat/async`: сколько миллисекунд держать submit в ожидании быстрого AI-ответа. Default `2500`, min `100`, max `30000`; вне диапазона → `400 INVALID_ASYNC_WAIT`. Если одновременно передан header `Prefer: respond-async, wait=N`, body-поле имеет приоритет |
| assistMode | string | нет | Режим работы AI (F-000041, REQ-022, v0.77.0): `planning`\|`auto`\|`review` — серверный гейт tool-surface + prompt-пресет; отсутствие поля = `auto`, невалидное значение = `auto` + warn (не 400). Действует только для `swan_code_client`; per-turn — шлите в каждом запросе хода. Подробно: [client-api-toolsurface.md](client-api-toolsurface.md), секция «Режимы работы» |
| clientTools | string[] | нет | **Negotiation client-executed тулов (F-000042, REQ-057, v0.80.0):** canonical-имена тулов, которые ЭТОТ клиент умеет исполнить. Эффективный surface хода = server-executed + пересечение(каталог продукта, заявка). **Ключ отсутствует** → полный каталог (byte-for-byte прежнее поведение); **`[]`** → ноль client-тулов (server-executed остаются). Неизвестные имена/дубли игнорируются (не 400); >256 имён — хвост игнорируется + warn. Тул-связки (write-тул без применяющего) сервер сужает сам. Per-request: продолжение хода шлёт своё поле; async-ход снапшотит на submit. Подробно: [client-api-toolsurface.md](client-api-toolsurface.md), секция «Negotiation clientTools» |
| queryText | string | нет | Текущий полный нормализованный текст запроса консоли (F-000014). Сервер пассивно хеш-сверяет и пишет новую версию в таблицу версий по происхождению (`query_versions_server`/`query_versions_client`, C3) при изменении. **Present-vs-absent (T-000244, v0.61.1):** ключ ОТСУТСТВУЕТ → no-op (старый клиент, backward-compat); ключ ПРИСУТСТВУЕТ, включая `""` → авторитетная актуализация — пустой редактор тоже состояние, пишется версия «пустой запрос» (hash-дедуп не плодит повторные пустые). В контекст ИИ не идёт |
| queryValidation | object | нет | Валидность `queryText` по проверке консоли (T-000245, v0.61.1): `{valid?: bool, skdValid?: bool, error?: string}`. `skdValid` — валидность для СКД. Пишется в версию (иммутабельно с ней), отдаётся в query-versions (admin + user). Отсутствие поля/подполей → NULL («не проверялось»). Осмысленно только рядом с присутствующим `queryText` |

**Удалено в v0.33.0:** поле `apiKey` (пользовательский API-ключ в обход серверного) — direct-провайдеров больше нет, Router per-request ключи не принимает. Присланное поле молча игнорируется.

**model-lock (T-000089; снят T-000247, v0.62.0):** ранее для `query_console_1c` смена `model` в существующей (непустой) сессии отклонялась `400 SESSION_MODEL_LOCKED`. **С v0.62.0 lock снят — модель можно менять внутри диалога у всех продуктов.** Кросс-модельная непрерывность держится сервером: `reasoning_details` эхаются обратно в Router только для ходов текущей модели, чужие вендор-специфичные подписи отбрасываются. `SESSION_MODEL_LOCKED` больше не возвращается (код сохранён в `apperrors` как точка расширения). KV-кеш при смене модели инвалидируется — ожидаемо.

**version-history (F-000014):** если в теле есть `queryText`, сервер пассивно версионирует текст запроса сессии (полный текст + структурный дифф + автор). `author=agent` проставляется, когда правка пришла от агентского `apply_query_change` (через backfill висящего apply на следующем ходе), иначе `user`. Чтение версий — `GET /v1/api/sessions/{id}/query-versions` (см. ниже).

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "sessionId": "a1b2c3d4-...",
  "title": "Обороты по счёту 60 за период",
  "content": "Ответ нейросети без tool-блоков...",
  "model": "anthropic/claude-sonnet-4",
  "modelDisplayName": "Claude Sonnet 4",
  "promptTokens": 150,
  "completionTokens": 500,
  "totalTokens": 650,
  "cachedTokens": 96,
  "codeChanges": [
    {
      "file": "main.go",
      "operation": "replace",
      "description": "Fix bug in handler",
      "searchB64": "Zm10LlByaW50bG4oIm9sZCIp",
      "replaceB64": "Zm10LlByaW50bG4oIm5ldyIp",
      "toolUseId": "tu_b3f4a12e5c6d"
    }
  ],
  "executeCommands": [
    {
      "command": "go build ./...",
      "description": "Build the project",
      "toolUseId": "tu_f1a2b3c4d5e6"
    }
  ],
  "toolUses": [],
  "serverToolProgress": [
    { "name": "load_skill", "input": { "name": "buh_skill" }, "isError": false }
  ],
  "balanceCoins": 19850.5,
  "costCoins": 12.5,
  "costUsd": 0.0625,
  "costRub": 6.25,
  "userMessageId": 4242,
  "reasoningText": "Нужно посчитать обороты по счёту 60...",
  "reasoningEffort": "high",
  "genMs": 8420,
  "tokensPerSec": 59.4,
  "contextFill": {
    "model": "anthropic/claude-sonnet-4",
    "lastPromptTokens": 41500,
    "contextWindow": 200000,
    "fillRatio": 0.2075,
    "compactionThresholdTokens": 141666,
    "thresholdRatio": 0.7083
  }
}
```

**Поле `cachedTokens` (REQ-027 swan_code_client, T-000312, опциональное, v0.80.0):** prompt-токены этого хода, прочитанные из кэша провайдера (prompt caching, v0.73.0): `⊆ promptTokens`, биллятся по сниженной ставке — «из 9 733 входных 6 528 пришли из кэша». **Контракт присутствия:** `omitempty`, присутствует ⟺ значение > 0; отсутствие = «нет данных о кэше» (первый ход диалога / модель без кэша / opaque-провайдер не отдаёт разбивку) — клиент просто не рисует строку про кэш. Те же поля — в `result` при `completed` (async), в ответах `/chat/tool-results[/async]` и `/chat/tool-retry`, в `event: done` стрима и per-assistant-ход в `GET /v1/api/sessions/{id}/messages` (reload, симметрия live↔история). Семантика идентична `summary.cachedTokens` (`GET /v1/api/sessions/{id}/summary`), но на ход, а не Σ по диалогу.

**Поля `reasoningText` / `reasoningEffort` (REQ-008 / REQ-007, опциональные, v0.59.0):** размышления модели этого хода. `reasoningText` (string) — человекочитаемый текст размышлений (нормализован Router'ом; сырой опаковый `reasoning_details` клиенту не отдаётся). `reasoningEffort` (string, `none\|minimal\|low\|medium\|high`) — **фактически применённый** уровень (эхо Router после мягкого клэмпа: на не-reasoning модели вернётся `none`, даже если запрошен `high`). Оба `omitempty` — присутствуют ⟺ в ходе реально были размышления. Те же поля — per-assistant-ход в `GET /v1/api/sessions/{id}/messages` (реплеятся при перезагрузке диалога) и в `result` при `completed`. **Caveat (сторона Router):** Gemini через провайдера ProxyAPI даёт upstream-400 на `reasoningEffort`>none (их шлюз без `thinkingConfig`); через BotHub — штатно.

**Поле `title` (REQ-021 Lensa_Query, опциональное):** заголовок диалога. На **создании** сессии (первый обмен без `sessionId`) — непустой, выведён из первого сообщения пользователя (схлопывание пробелов + обрезка до 60 символов); убирает «прыжок» имени на клиенте, который иначе ставит временное «Диалог N» и подтягивает серверное имя отдельным запросом метаданных. На **продолжении** существующей сессии — текущий заголовок (актуален и после переименования). `omitempty`: опускается, если заголовок не посчитан (вырожденный ввод). Клиент читает мягко — отсутствие поля не ломает. То же поле — в `result` ответа `GET /v1/api/chat/{ticketId}/result` при `completed`.

**Поле `userMessageId` (T-000107, опциональное):** SERIAL id персистнутого сообщения пользователя этого хода — стабильный якорь user-хода (равен `id` соответствующего сообщения в `GET /v1/api/sessions/{id}/messages`). Позволяет клиенту действовать на конкретный user-ход (attribution версий, retry) без отдельного запроса истории. Опускается (`omitempty`), если сообщение не удалось сохранить (id=0).

**Поля `balanceCoins` / `costCoins` (T-000106, опциональные):**
- `balanceCoins` (float) — остаток монет пользователя **после** списания за этот ход. Равен `balanceCoins` из `GET /v1/api/me` (gross — НЕ вычитает активные резервы параллельных ходов). Клиент обновляет баланс в шапке без отдельного запроса к `/me`.
- `costCoins` (float) — стоимость этого хода в монетах (списание с наценкой).
- **Контракт присутствия:** оба поля присутствуют **⟺ реальное списание (commit) прошло**. Отсутствуют (`omitempty`) при отключённом биллинге, непосчитанной цене модели или фейле commit'а. **Отсутствие читать как «неизвестно», НЕ как «бесплатно»/«ноль»** — при нулевом балансе поле присутствует со значением `0`.

**Поля `costUsd` / `costRub` (T-000210, опциональные):** стоимость этого хода в USD и рублях по **настоящему** курсу — `costUsd = costCoins ÷ pricing.coins_per_usd`, `costRub = costCoins ÷ pricing.coins_per_rub`. Это **трата клиента** (что он заплатил по курсу; наценка уже зашита в `costCoins`). Тот же контракт присутствия, что у `costCoins` (есть ⟺ commit прошёл). **Закупочную цену Router'а (COGS) сервер клиенту НЕ отдаёт** — она доступна только в админ-эндпоинтах (`economics`, см. `GET /v1/admin/sessions`). Те же поля — в `result` ответа `GET /v1/api/chat/{ticketId}/result` и в ответах `/chat/tool-results` / `/chat/tool-retry`.

**Поле `contextFill` (REQ-053 Lensa_Query, F-000037, опциональное, v0.75.0):** занятость контекстного окна **после этого хода** — телеметрия для кольца/тултипа контекста, **ОТДЕЛЬНО от биллинговых счётчиков** (`promptTokens`/`completionTokens` — трата хода; `contextFill` — текущее наполнение). Объект: `model` (фактическая модель вызова), `lastPromptTokens` (prompt-токены финального AI-вызова хода = полный отправленный контекст: system prompt + tools + история), `contextWindow` (окно модели из каталога), `fillRatio = lastPromptTokens/contextWindow` (может быть >1 при устаревшем каталоге — клиент клампит), `compactionThresholdTokens` + `thresholdRatio` — **реальная точка срабатывания авто-компактизации** (с v0.80.0 — точный заданный лимит: absolute-лимит продукта как есть, либо окно × threshold_pct): рисуйте отметку порога от них, тогда кольцо совпадёт с фактическим поведением сервера. Порог опущен (`omitempty`), если компактизация off для продукта. **Degraded-форма (T-000315, REQ-028, v0.80.0):** если окно модели неизвестно каталогу, но у продукта задан absolute-лимит компактизации — объект приходит **без** `contextWindow`/`fillRatio`/`thresholdRatio` (только `model`, `lastPromptTokens`, `compactionThresholdTokens`): рисуйте кольцо от `lastPromptTokens / compactionThresholdTokens`. Весь объект опущен, только если ход не измерен ИЛИ неизвестны и окно, и порог — отсутствие читать как «нет данных». Те же поля: в `result` при `completed` (async), в ответах `/chat/tool-results[/async]` и `/chat/tool-retry`, в `event: done` стрима, и session-level в `GET /v1/api/sessions/{id}/messages` (reload).

**Поля `genMs` / `tokensPerSec` (REQ-040 Lensa_Query, T-000242, опциональные, v0.60.1):** время и скорость генерации финального AI-вызова хода. `genMs` (int) — окно генерации в миллисекундах, замеряется сервером как elapsed sync-вызова Router. `tokensPerSec` (float) — скорость, считается **сервером**: `completionTokens ÷ (genMs/1000)`. **Контракт присутствия:** `omitempty`, присутствуют ⟺ окно измерено; отсутствие = «нет данных», не ноль. Не измеряется (поля опущены): промоутнутый async-тикет (Router отдал `202`, ответ пришёл поллингом — окно неизвестно); `tokensPerSec` дополнительно опущен при нуле completion-токенов. Те же поля — в `result` при `completed`, в ответах `/chat/tool-results` / `/chat/tool-retry` и per-assistant-ход в `GET /v1/api/sessions/{id}/messages`.

| Поле | Тип | Описание |
|------|-----|----------|
| model | string | Технический ID модели (равен полю `model` в запросе) — используется биллингом и системой recommendations |
| modelDisplayName | string | Человекочитаемое имя для UI («Claude Opus 4.6», «GPT-5»). Резолв (T-000250, override-aware): **admin-оверрайд** (`model_display_overrides`) по фактической, затем запрошенной модели → каталог Router → `HumanizeName(id)`. Оверрайд — та же авторитетная админ-метка, что в списке моделей (`GET /v1/api/models`) и админ-прайсе; чат/`/chat/tool-results[/async]`/история совпадают с ними. **Паритет форм (T-000248):** датированный real-снапшот (`…-20260519`, нет в каталоге) ловится по запрошенной модели (каталожный ключ, одинакова во всех ответах хода). Клиент может выводить `modelDisplayName ?? model`. Поле `omitempty` |
| codeChanges | array | Структурированные изменения кода из ответа (если есть). Клиент должен декодировать `searchB64`/`replaceB64` из base64 перед применением. Заполняется только если AI вызвал `apply_code_change` (только для default-продукта); для не-default продуктов — всегда `[]` |
| executeCommands | array | Команды для выполнения из ответа (если есть). Заполняется только если AI вызвал `execute_command` (только для default-продукта); для не-default продуктов — всегда `[]` |
| toolUses | array | Generic-канал tool_use'ов (все non-legacy тулы, см. `Tool surface` ниже). Для default-продукта v0.23.0+ — заполнен для `oneC_*` UIA-тулов; для query_console_1c — для всех 20 console-тулов; legacy `apply_code_change`/`execute_command` сюда НЕ попадают (они в `codeChanges`/`executeCommands`). Сериализуется как массив (не `omitempty`) |
| serverToolProgress | array | **REQ-046 Lensa_Query (v0.65.0).** «Показываемые» server-executed тулы — **non-actionable** маркеры для строки прогресса на живом ходе. Клиент их **НЕ исполняет** (они исполнены сервером), только рисует прогресс (напр. `load_skill` → «Подключаю навык: <input.name>»; `get_latest_query_version` → «Сверяю сохранённую версию запроса»). Элемент: `{name, input, isError?}` — тот же shape, что `toolCalls` в `GET /v1/api/sessions/{id}/messages`. Opt-in по тулу (решает сервер): показываются `load_skill` и `get_latest_query_version` (REQ-049, `v0.66.1`). `isError` (`*bool`) — исход, присутствует ⟺ для вызова есть tool_result. **Семантика — delta-per-response (REQ-050b, `v0.66.1`):** каждый ответ несёт **только** server-tools своего раунда (исполненные именно в этом ответе), НЕ turn-cumulative и НЕ re-emit из персиста. Накопленный за весь ход набор (по всем ответам сегментов `/chat` → `/chat/tool-results[/async]`) == reload `toolCalls` того же хода — симметрия live↔reload без дублей и без пропусков. `omitempty`: присутствует ⟺ в этом ответе исполнялся показываемый server-tool (обычно только у query_console_1c); иначе отсутствует. Те же поля — в ответах `/chat/async` (`result`), `/chat/tool-results[/async]` |

**Поля `codeChanges[]`:**

| Поле | Тип | Описание |
|------|-----|----------|
| file | string | Путь к файлу относительно корня проекта |
| operation | string | `replace` \| `create_file` |
| description | string | Краткое описание изменения |
| searchB64 | string | base64 от точного фрагмента для поиска. Для `create_file` отсутствует (`omitempty`) |
| replaceB64 | string | base64 от нового содержимого (`replace`) или полного файла (`create_file`) |
| toolUseId | string | Идентификатор вызова инструмента. Используется в retry-запросах для сообщения об ошибках применения |

**Поля `executeCommands[]`:**

| Поле | Тип | Описание |
|------|-----|----------|
| command | string | Shell-команда |
| description | string | Краткое описание, что и зачем выполняется |
| toolUseId | string | Идентификатор вызова, используется в retry-запросах |

**Почему base64:** `searchB64` и `replaceB64` передаются кодированными, чтобы исключить зависимость клиентского JSON-парсера от содержимого кода. Base64-алфавит (`A-Za-z0-9+/=`) не содержит ни кавычек, ни backslash, ни управляющих символов — клиент гарантированно не споткнётся на 1С-коде с кириллицей, табами и операторами `<>`.

**Логика сессий:**
- Первый запрос без `sessionId` — сервер создаёт сессию, возвращает `sessionId`.
- Последующие запросы с `sessionId` — сервер добавляет сообщение к контексту диалога.
- Клиент отправляет только последнее сообщение, сервер хранит весь контекст.

---

## POST /v1/api/chat/async

Async submit для долгих chat-ходов (v0.51.0, F-000030). Request body совпадает с `POST /v1/api/chat`, дополнительно принимает `asyncWaitMs`.

Поведение:
- Если Router/AI успел ответить за `asyncWaitMs`, сервер возвращает обычный `ChatResponse` с HTTP `200`.
- Если ответ не успел, сервер возвращает HTTP `202` с локальным `ticketId`; клиент продолжает через `GET /v1/api/chat/{ticketId}/result`.
- Сервер держит billing hold с submit и делает settle/release при terminal result.
- Assistant message, usage_stats и `sessions.model/temperature` пишутся только при successful completed result.

Дополнительный стандартный header:

```http
Prefer: respond-async, wait=3
```

`wait` задаётся в секундах; `asyncWaitMs` в body имеет приоритет.

**Request:**

```json
{
  "sessionId": "",
  "model": "anthropic/claude-sonnet-4.6",
  "message": "Сформируй запрос по остаткам",
  "temperature": 0.7,
  "queryText": "ВЫБРАТЬ ...",
  "asyncWaitMs": 2500
}
```

**Response 202:**

```json
{
  "serviceInfo": {"success": true},
  "ticketId": "2df9c7fc-19e8-4c99-9a83-bb2185fcff31",
  "status": "pending",
  "sessionId": "a1b2c3d4-...",
  "title": "Обороты по счёту 60 за период",
  "userMessageId": 4242
}
```

**Поле `title` (REQ-029 Lensa_Query, опциональное):** заголовок диалога — тот же, что придёт позже в `result.title` на `completed`. Считается на создании сессии (submit'е), поэтому доступен уже в `202`. Симметрично синхронному `/chat` (REQ-021): позволяет клиенту на 202-пути (медленная модель) материализовать строку диалога сразу с именем, без «прыжка» «Диалог N» → серверный заголовок. `omitempty`: опускается при непосчитанном заголовке (вырожденный ввод); клиент читает мягко. Присутствует и в `202` продолжения через `POST /v1/api/chat/tool-results/async`.

Ошибки preflight (`INVALID_JSON`, `MODEL_REQUIRED`, `INVALID_TEMPERATURE`, `INVALID_ASYNC_WAIT`, `MODEL_NOT_ALLOWED`, `MESSAGE_TOO_LONG`, `REQUEST_TOO_LARGE`, billing errors) возвращаются синхронно, как у `POST /v1/api/chat`.

## GET /v1/api/chat/{ticketId}/result

Polling результата async chat-хода. Требует тот же `Authorization` + `X-Product-Key`; чужой ticket возвращает `404 TICKET_NOT_FOUND`.

**Pending 200:**

```json
{
  "serviceInfo": {"success": true},
  "ticketId": "2df9c7fc-19e8-4c99-9a83-bb2185fcff31",
  "status": "pending",
  "sessionId": "a1b2c3d4-..."
}
```

**Completed 200:**

```json
{
  "serviceInfo": {"success": true},
  "ticketId": "2df9c7fc-19e8-4c99-9a83-bb2185fcff31",
  "status": "completed",
  "sessionId": "a1b2c3d4-...",
  "result": {
    "serviceInfo": {"success": true},
    "sessionId": "a1b2c3d4-...",
    "title": "Обороты по счёту 60 за период",
    "content": "Готово",
    "model": "anthropic/claude-sonnet-4.6",
    "promptTokens": 100,
    "completionTokens": 50,
    "totalTokens": 150,
    "codeChanges": [],
    "executeCommands": [],
    "toolUses": []
  }
}
```

**Failed/abandoned 200:**

```json
{
  "serviceInfo": {"success": false, "errorCode": "AI_REQUEST_FAILED"},
  "ticketId": "2df9c7fc-19e8-4c99-9a83-bb2185fcff31",
  "status": "failed",
  "sessionId": "a1b2c3d4-...",
  "errorCode": "AI_REQUEST_FAILED",
  "errorMessage": "Ошибка запроса к AI"
}
```

Terminal `failed`/`abandoned` — это polling status и поэтому HTTP `200`; transport/preflight ошибки самого polling endpoint остаются обычными HTTP errors.

**Canceled 200 (v0.75.0):** поллинг отменённого тикета — терминал `{"status": "canceled"}` (success=true, без `result`/`errorCode`). См. cancel-ручку ниже.

---

## POST /v1/api/chat/{ticketId}/cancel

**Status: implemented (v0.75.0, F-000037/REQ-053).** Отмена in-flight async-хода. **Идемпотентна**, финальное состояние различимо от ошибки: success всегда `true`, исход — в полях.

**Request:** без тела. Тот же auth (`swan_uk_*` + `X-Product-Key`); чужой/несуществующий тикет → `404 TICKET_NOT_FOUND`, кривой uuid → `400 INVALID_TICKET_ID`.

**Response 200:**

```json
{
  "serviceInfo": {"success": true},
  "ticketId": "2df9c7fc-19e8-4c99-9a83-bb2185fcff31",
  "sessionId": "a1b2c3d4-...",
  "status": "canceled",
  "canceled": true
}
```

- `canceled: true` — ход отменён **этим вызовом**: резерв баланса освобождён сразу (списания за отменённый ход нет), поздний ответ AI выбрасывается (в историю не пишется). `status = canceled`.
- `canceled: false` — тикет уже в другом состоянии; фактический статус в `status`: `completed`/`failed`/`abandoned` (ход успел завершиться — результат доступен обычным поллингом), `canceled` (повторный cancel — идемпотентно), `pending` (финализация уже пишет результат — отменять поздно, ход завершится; при желании заберите результат поллингом).
- Отмена останавливает **наш** ход; upstream-вызов у AI-провайдера не прерывается — его результат сервер выбрасывает.

---

## POST /v1/api/chat/stream

**Status: implemented (v0.63.0, REQ-016 / T-000141).** Нормализованный SSE-стрим ответа AI:
сервер проксирует Router-SSE и эмитит **унифицированный** поток событий, не зависящий от
провайдера (клиенту НЕ нужно знать формат Anthropic/OpenAI/GigaChat). **Text-only display-путь:**
тулы в стрим НЕ приходят (по дизайну) — для tool-циклов (code_changes / console-тулы) клиент
использует `POST /v1/api/chat[/async]`. Money-path тот же, что у sync-хода: pre-reserve →
стрим → commit фактической стоимости (обрыв/ошибка → release, не charge).

**Request:** тело идентично `POST /v1/api/chat` (`sessionId?`, `message`, `model`, `temperature?`,
`reasoningEffort?`, `queryText?`, `queryValidation?`). Заголовки — как у chat (`swan_uk_*` +
`X-Product-Key` + `X-QC-Token` для `query_console_1c` при enforce).

**Response (200):** `Content-Type: text/event-stream`, заголовок `X-Session-ID`. Поток событий:

```
event: chunk
data: {"text":"<очередной текстовый кусок>"}

event: chunk
data: {"text":"<кусок размышлений>","kind":"thinking"}   // reasoning-дельта; kind отсутствует = обычный текст

event: done
data: {"sessionId":"...","promptTokens":150,"completionTokens":500,"totalTokens":650,"contextFill":{...}}
```

- `chunk.text` всегда непустой (пустые дельты подавляются сервером — клиент не аппендит `""`).
- `kind:"thinking"` — reasoning-дельта модели с extended thinking; клиент может отображать
  отдельным стилем либо игнорировать. По умолчанию `kind` отсутствует = обычный текст ответа.
- Терминал успеха — **ровно один** `event: done` с агрегированным usage хода.
- Конкатенация всех `chunk.text` (без `thinking`) ≈ `content` не-стримингового `POST /chat`
  той же модели (±whitespace).

**Ошибка ПОСЛЕ старта SSE** (upstream-фейл / насыщение) — терминальный `event: error`
(HTTP-статус уже `200`, поэтому исход идёт event'ом):

```
event: error
data: {"errorCode":"UPSTREAM_SATURATED","message":"..."}
```

**Errors (до старта SSE — обычный HTTP-код, SSE ещё не начат):**
- `EMPTY_MESSAGE` / `MODEL_REQUIRED` / `INVALID_TEMPERATURE` / `INVALID_REASONING_EFFORT` / `MESSAGE_TOO_LONG` (400), `REQUEST_TOO_LARGE` (413)
- `INSUFFICIENT_BALANCE` (402) — не хватает баланса на pre-reserve
- `STREAMING_NOT_SUPPORTED` (501) — провайдер не поддерживает стрим (клиент фолбэкает на `/chat`);
  либо `405` от Router, если у модели нет стримящего кандидата
- `MODEL_NOT_ALLOWED` (403), `AI_UNAVAILABLE` (503), `AI_TIMEOUT` (504), `AI_REQUEST_FAILED` (502)
- `AI_BUSY` (429) — провайдеры перегружены (транзиент; сервер уже ретраил сам — клиенту безопасно повторить через 1–2 с), `MODEL_UNAVAILABLE` (503) — модель снята с апстрима (терминально, предложить другую модель) — оба v0.73.1, применимы ко всему семейству `/v1/api/chat*`
- `AI_EMPTY_RESPONSE` (502) — AI вернул пустой completed-ответ (ни текста, ни тулов, ни reasoning): апстрим-глюк, средства не списаны, безопасно повторить (REQ-058, v0.82.0). Применим ко всему семейству `/v1/api/chat*`: sync-пути — HTTP 502; async-поллинг — терминал `status=failed` с этим кодом; стрим — `event: error` после пустого финала

**Side effects:**
- Резерв баланса под максимум → commit по фактическому usage финального фрейма (обрыв клиента
  или ошибка → release, списания нет).
- Персист user-сообщения (на старте) и assistant-текста + `usage_stats` (по завершении, только
  при успешном `done`; при обрыве частичный текст НЕ персистится).
- Смена модели/температуры хода фиксируется в сессии (как у `/chat`).

---

