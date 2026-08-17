# Client API — Sessions & Updates

> Часть client-контракта. Индекс: [client-api.md](client-api.md).

# Public API — Диалоги и история (требует `swan_uk_*` + `X-Product-Key: swan_pk_*`)

Мультидиалоговая консоль (v0.27.0). Все эндпоинты строго `user+product`-scoped: фильтр по `user_id` + `product_id` из контекста, чужая/несуществующая сессия → `404 SESSION_NOT_FOUND`.

## GET /v1/api/sessions

Список диалогов пользователя в рамках продукта. С v0.52.0 дефолтный режим — cursor-пагинация по активности: `?limit=50&cursor=<nextCursor>`, сортировка `lastMessageAt DESC, id DESC`. Явные `page`/`size` без `cursor`/`limit` включают legacy offset-режим (`size` max 100) для старых клиентов.

**Response (200):**
```json
{
  "serviceInfo": {"success": true},
  "sessions": [
    {
      "id": "uuid",
      "title": "Остатки по складу",
      "model": "anthropic/claude-sonnet-4",
      "createdAt": "RFC3339",
      "lastMessageAt": "RFC3339",
      "lastMessagePreview": "первые ~80 символов последнего видимого сообщения"
    }
  ],
  "total": 42,
  "hasMore": true,
  "nextCursor": "base64url-json",
  "limit": 50
}
```
В cursor-режиме `total` остаётся для совместимости, `page/size` равны `0`; в legacy offset-режиме остаются `total`, `page`, `size`, а `hasMore/nextCursor/limit` опускаются. `lastMessageAt` — денорм активности сессии; если сообщений нет, равен `createdAt`.

`title` авто-инициализируется из первого сообщения пользователя (обрезка ~60 символов); переименовывается через `PATCH /v1/api/sessions/{id}`.

## POST /v1/api/sessions/batch

Метаданные диалогов по списку ID одним запросом (T-000117). Назначение — освежить имена/модели сохранённых локально диалогов при открытии файла консоли, не вытягивая всю ленту (`GET /v1/api/sessions` пагинируется, size max 100). POST (не `GET ?ids=`), т.к. список ID может быть длинным и не влезть в URL.

**Request:**
```json
{ "ids": ["uuid1", "uuid2", "..."] }
```
- `ids` — `[string]`, обязателен. Cap **200 distinct** на запрос. Дубли схлопываются, пустые/пробельные строки отбрасываются.

**Response (200):**
```json
{
  "serviceInfo": {"success": true},
  "sessions": [
    {"id": "uuid", "title": "Остатки по складу", "model": "anthropic/claude-sonnet-4", "createdAt": "RFC3339"}
  ]
}
```
- Возвращаются **только** запрошенные и принадлежащие `user+product` сессии. Отсутствующие / чужие / невалидные (не-uuid) ID просто не попадают в ответ — **не ошибка**, не 404 (security-граница: продукт A не пробит сессии продукта B).
- Без `lastMessagePreview` (в отличие от `GET /v1/api/sessions`) — дорогой подзапрос, для рефреша имён/моделей не нужен.
- Порядок `sessions` не гарантирован — клиент матчит по `id`. Пустой `ids: []` → `200` с пустым массивом.

**Errors:**
- `400 INVALID_JSON` — нераспарсиваемое тело.
- `400 TOO_MANY_IDS` — больше 200 distinct ID в одном запросе (reject, не silent-truncate; разбейте на пачки).

## GET /v1/api/sessions/{id}/messages

История чата диалога. Курсор-пагинация: `?limit=50&before=<msgId>`. Включает `visible=true` сообщения **и** скрытые `assistant_tool_use` (для отрисовки tool-маркеров, T-000108); `tool_result`/`system` клиенту не отдаются.

**Response (200):**
```json
{
  "serviceInfo": {"success": true},
  "session": {"title": "Отбор товаров", "model": "anthropic/claude-sonnet-4"},
  "messages": [
    {"id": 123, "role": "user", "kind": "user_text", "content": "...", "createdAt": "RFC3339"},
    {"id": 124, "role": "assistant", "kind": "assistant_tool_use", "content": "Смотрю таблицы запроса", "createdAt": "RFC3339",
     "model": "google/gemini-3.5-flash", "temperature": 0, "realModel": "google/gemini-3.5-flash-001",
     "reasoningText": "Пользователь просит разбор — начну с состава таблиц.",
     "toolCalls": [{"name": "get_query_tables", "input": {"tableIndex": 0}, "isError": false}],
     "promptTokens": 1820, "completionTokens": 95, "totalTokens": 1915, "costCoins": 0.123},
    {"id": 125, "role": "assistant", "kind": "assistant_text", "content": "Готово", "createdAt": "RFC3339",
     "model": "anthropic/claude-sonnet-4.6", "temperature": 0.7,
     "promptTokens": 2010, "completionTokens": 140, "totalTokens": 2150, "costCoins": 0.146,
     "costUsd": 0.00073, "costRub": 0.073, "modelDisplayName": "Claude Sonnet 4.6",
     "genMs": 8420, "tokensPerSec": 59.4}
  ]
}
```
- `session` (T-000108) — заголовок/модель диалога (= поля из `GET /v1/api/sessions`), чтобы загрузка по `id` давала имя+модель inline. `omitempty`.
- `messages[].toolCalls` (T-000108) — для `kind=assistant_tool_use`: вызовы инструментов хода (`name` + `input` тула) для строк прогресса. Сырые tool_use/tool_result блоки не отдаются. `omitempty` (отсутствует у текстовых сообщений).
  - `toolCalls[].isError` (bool, REQ-027 Lensa_Query, `v0.57.0`) — исход выполнения тула: `true` = тул отработал с ошибкой (для мутирующих `apply_query_change`/`replace_table`/… чаще всего валидация запроса не прошла) → клиент рисует ✗; `false` = успех → ✓. Джойн по `toolUseId` с `tool_result` сессии (данные уже хранятся — surface existing, не новая модель; содержимое `tool_result` клиенту по-прежнему не отдаётся, только булев исход). **`omitempty`: присутствует ⟺ для вызова найден `tool_result`** — опускается для старых записей без результата и на живом ходе до прихода результата (клиент читает отсутствие мягко: рисует ✓/нейтрально). Живой поток исход знает из payload'а; это поле закрывает разрыв при перезагрузке диалога из истории. **REQ-048 (`v0.66.1`):** для `validate_query` с `valid:false` (запрос невалиден) поле проецируется в `isError:true`, хотя «сырой» tool_result-исход `false` (сам вызов валидатора успешен) — паритет с живым ходом, который на невалидном запросе уже рисует ✗. Прочие тулы не затронуты.
- `messages[].content` у `kind=assistant_tool_use` — **текстовый комментарий модели к вызовам** («Смотрю таблицы запроса»): персистится и отдаётся ровно тот текст, что пришёл в `content` живого ответа `/chat` вместе с тулами (проверено на живых данных, REQ-026, `v0.79.1`). Пустая строка = модель промолчала в `content` на этом ходе — **это частый легитимный кейс у reasoning-моделей** (glm и др.): пояснение хода модель кладёт в размышления, см. `reasoningText` ниже.
- `messages[].reasoningText` / `reasoningEffort` (REQ-008/REQ-007, `v0.59.0`; ранее не были задокументированы в этом топике — REQ-026, `v0.79.1`) — текст размышлений assistant-хода и фактически применённый уровень. **История ОТДАЁТ `reasoningText`** — включая `assistant_tool_use`-строки: на «молчаливых» tool-ходах reasoning-моделей это единственный носитель пояснения (в live-ленте пользователь видит именно его). Клиенту при reload рекомендуется рендерить `reasoningText` тем же образом, что на живом ходе — иначе диалог теряет комментарии. `omitempty` у `reasoningText`: опущен у ходов без размышлений и у строк до v0.59.0. **`reasoningEffort` с `v0.80.0` (T-000310, REQ-025) персистится и отдаётся у КАЖДОГО assistant-хода, включая `none`** («размышления выключены») — симметрично `temperature`: клиент восстанавливает чип уровня из истории, своё хранилище не нужно. Отсутствие поля = строка записана до v0.80.0 — читать как «неизвестно» и падать на дефолт проекта, НЕ на `none`.
- `messages[].model` / `temperature` / `realModel` (v0.50.0, F-000026) — attribution assistant-хода: requested model, requested temperature (explicit `0.0` is preserved), and actual Router/provider model if it differs from requested. Поля опускаются для legacy/user/tool rows без attribution; `realModel` опускается, если совпал с `model`.
- `messages[].promptTokens` / `completionTokens` / `totalTokens` / `costCoins` (T-000146) — расход AI-вызова, породившего это assistant-сообщение (`int` токены вход/исход/всего + стоимость в коинах). **Присутствуют ⟺ для сообщения есть usage-строка** (assistant-ход); у `user`/tool-сообщений опущены. Контракт присутствия как у `/chat`: отсутствие = «нет данных», **не** «ноль». `costCoins` — та же монета, что `costCoins`/`balanceCoins` в `/chat`. Удобно для отображения расхода прямо в ленте истории.
- `messages[].cachedTokens` (REQ-027, T-000312, `v0.80.0`) — cache-read prompt-токены того же AI-вызова: `⊆ promptTokens`, биллились по сниженной ставке. `omitempty`, присутствует ⟺ значение > 0 — отсутствие = «нет данных о кэше» (ход без кэша / opaque-провайдер / строки до v0.73.0), клиент не рисует строку про кэш. Симметрия с live-ходом (`cachedTokens` в `/chat*`) — показатель не исчезает при перезагрузке диалога.
- `messages[].costUsd` / `costRub` / `modelDisplayName` (REQ-040 Lensa_Query, T-000242, `v0.60.1`) — паритет истории с live-ходом: `costUsd`/`costRub` — та же трата хода по **текущему** курсу продукта (`costCoins ÷ pricing.coins_per_usd|rub`; если курс менялся с момента хода — значения приблизительны, как у админ-`economics`; присутствуют вместе с `costCoins`); `modelDisplayName` — человекочитаемое имя фактической модели хода (`realModel`, fallback `model`) — клиент рисует чип модели без самостоятельной гуманизации технического id. `omitempty`.
- `messages[].genMs` / `tokensPerSec` (REQ-040, T-000242, `v0.60.1`; async-фикс REQ-044, `v0.63.0`) — окно и скорость генерации AI-вызова этого сообщения; семантика и контракт присутствия — см. `POST /v1/api/chat`. **Промоутнутые async-ходы теперь тоже несут окно** (REQ-044): sync и sync-fast async — точное окно генерации; промоутнутый async с `v0.73.0` (T-000252) — **точное окно обработки Router** (`completed_at − created_at` из poll-ответа, REQ-010), фолбэк — end-to-end окно `create→finalize` (если Router-тайминги не пришли). Опущены только у исторических строк до v0.60.1 (окна нет вовсе).
- Сообщения отсортированы по возрастанию `id` (хронологически). Для подгрузки более старых — `before` = `id` первого (верхнего) сообщения страницы. **Строки `role=checkpoint` при вычислении курсора пропускайте** (у них `id=0`, см. ниже).
- `id` — `SERIAL` (integer); в выдаваемом наборе порядок `id` совпадает с хронологией.
- **`role=checkpoint` / `kind=checkpoint_summary`** (R1 T-000268, `v0.68.0`; правило вставки уточнено `v0.70.3`) — **синтетическая** строка «контекст был сжат»: `content` = rolling-summary компактизации, `id=0` (не персистится в `messages`, вычисляется на лету из checkpoint'а). Появляется **только на странице, содержащей границу компактизации** (есть и покрытое summary сообщение, и более новое) — первой строкой страницы не встаёт, на прочих страницах не дублируется. Клиент, не знающий этой роли, может просто не рендерить незнакомые `role`.
  - **Checkpoint-рендер (REQ-053, F-000037, `v0.75.0`)** — поля для отрисовки checkpoint-строки: `checkpointTrigger` (`auto` — авто-триггер границы хода | `manual` — ручная компактизация), `tokensBefore` (занятость контекста на момент триггера), `tokensAfterEstimate` (**оценка** занятости после сжатия: summary + хвост; честного замера нет до следующего AI-вызова), `checkpointPass` (номер прохода; rolling-checkpoint один на сессию, pass растёт с каждым проходом и служит стабильным идентификатором внутри сессии; `createdAt` строки = момент последнего прохода). Ordering стабилен: строка стоит ровно на границе сжатия относительно обычных сообщений. Токен-поля опущены у проходов до v0.75.0 (`omitempty`, omit ≠ ноль). У compaction-строки **нет** `temperature`/`status` — этих понятий у summarizer-вызова не существует.
- **`contextFill`** (top-level, REQ-053, `v0.75.0`) — текущая занятость контекста сессии для reload: тот же shape и семантика, что `contextFill` в ответах `/chat` (см. `client-api-chat.md`), источник — свежайший измеренный AI-вызов сессии. `omitempty`: нет измеренных ходов / окно модели неизвестно.

**Errors:** `404 SESSION_NOT_FOUND` — сессия не принадлежит user+product.

## GET /v1/api/sessions/{id}/summary

Предагрегированный **итог по диалогу** (REQ-041 Lensa_Query, `v0.62.0`) — Σ токенов/монет/времени за **весь** диалог, посчитанный сервером по всем сообщениям (не ограничено `limit`/страницей). Для формы свойств диалога («суммарный расход»). Суперсидит клиентское суммирование из REQ-040 (страничная сумма занижала длинные диалоги + разъезд правил округления). Аудитория — пользовательский клиент: только клиентская трата (`costUsd`/`costRub`), без COGS/Router-закупки.

**Response (200):**
```json
{
  "serviceInfo": {"success": true},
  "session": {"title": "Отбор товаров", "model": "anthropic/claude-sonnet-4.6"},
  "summary": {
    "turnsCount": 5,
    "promptTokens": 18400, "completionTokens": 1230, "totalTokens": 19630,
    "costCoins": 1.42, "costUsd": 0.0071, "costRub": 0.71,
    "genMs": 41200, "tokensPerSec": 29.9,
    "modelsUsed": ["Gemini 3.5 Flash", "Claude Sonnet 4.6"],
    "byModel": [
      {"model": "google/gemini-3.5-flash", "modelDisplayName": "Gemini 3.5 Flash", "turnsCount": 3,
       "promptTokens": 12000, "completionTokens": 800, "totalTokens": 12800,
       "costCoins": 0.62, "costUsd": 0.0031, "costRub": 0.31},
      {"model": "anthropic/claude-sonnet-4.6", "modelDisplayName": "Claude Sonnet 4.6", "turnsCount": 2,
       "promptTokens": 6400, "completionTokens": 430, "totalTokens": 6830, "cachedTokens": 4100,
       "costCoins": 0.80, "costUsd": 0.0040, "costRub": 0.40}
    ]
  }
}
```
- `session` — заголовок/модель диалога (как в `/messages`), `omitempty`.
- `summary.turnsCount` (int) — число ходов диалога = visible `user_text` сообщений («пар запрос→ответ»). **Tool-loop одного запроса = ОДИН ход** (AI-подвызовы петли не считаются). Всегда присутствует (`0` для пустого/нового диалога).
- `summary.promptTokens` / `completionTokens` / `totalTokens` (int) — Σ токенов по всем assistant-ходам. Присутствуют ⟺ есть хотя бы один измеренный ход (usage-строка).
- `summary.cachedTokens` (int, T-000231 `v0.73.0`) — Σ prompt-токенов, **прочитанных из кэша провайдера** (prompt caching, REQ-013): «сколько контекста пришло из кэша» — ⊆ `promptTokens`, биллится по сниженной ставке. Присутствует ⟺ Σ>0 (диалоги без кэша / измеренные до v0.73.0 поле не отдают).
- `summary.costCoins` (число) — Σ списаний в монетах; `costUsd`/`costRub` — по **текущему** курсу продукта (та же конвертация, что per-message REQ-040; приблизительно при смене курса). Присутствуют ⟺ Σ стоимости > 0 (billing-skipped ходы не создают стоимость); `usd`/`rub` — при `costCoins` и соответствующем `rate>0`.
- `summary.genMs` (int) — Σ окон генерации; `tokensPerSec` (float) — **средняя** скорость диалога, посчитана сервером как `Σcompletion ÷ (ΣgenMs/1000)`. Присутствуют ⟺ `ΣgenMs>0` (и `Σcompletion>0` для `tokensPerSec`).
- `summary.modelsUsed` (array, опц.) — дружелюбные имена всех моделей диалога, дедуп, в порядке появления. Осмысленно после снятия model-lock (T-000247, мульти-модельные диалоги). Пустой список опущен.
- `summary.byModel` (array, опц., REQ-051 `v0.72.0`) — **per-model разбивка** расхода диалога, элемент на каждую модель с измеренным расходом, в порядке первого появления в диалоге. Поля элемента: `model` (канонический ID), `modelDisplayName` (override-aware, как `modelsUsed`), `turnsCount` (REQ-061 `v0.84.0`, см. ниже), `promptTokens`/`completionTokens`/`totalTokens`, `costCoins`/`costUsd`/`costRub` (та же конвертация и presence-правила, что тоталы), `cachedTokens` (T-000231 `v0.73.0` — Σ cache-read токенов модели, ⟺ Σ>0). **Инвариант:** Σ по элементам (cost/tokens) == соответствующий тотал `summary` (агрегат из одного SQL-снапшота). Пустой список опущен.
- `summary.byModel[].turnsCount` (int, REQ-061 `v0.84.0`) — число уникальных видимых пользовательских ходов, в которых у модели был **хотя бы один измеренный AI-вызов**. Семантика **участия, не владения**: несколько вызовов модели в tool-loop/retry одного хода = 1; ход с разными моделями учитывается у **каждой** из них, поэтому Σ `byModel[].turnsCount` может превышать общий `summary.turnsCount` — аддитивного инварианта у этого поля нет (в отличие от cost/tokens). Всегда присутствует и ≥ 1 (элемент `byModel` существует только у модели с измеренным расходом). Семантика общего `summary.turnsCount` не изменилась.
- **Контракт присутствия** как per-message (REQ-040): `omitempty`, присутствует ⟺ метрика измерима хотя бы по одному ходу; отсутствие = «нет данных», **не** ноль. Пустой диалог → `turnsCount:0`, денежные/токен-поля опущены.

**Errors:** `400 MISSING_ID`; `404 SESSION_NOT_FOUND` — сессия не принадлежит user+product.

## PATCH /v1/api/sessions/{id}

Переименование диалога.

**Request:** `{ "title": "string, required, обрезается до 200 символов" }`

**Response (200):** `{ "serviceInfo": {"success": true}, "title": "применённый title" }`

**Errors:** `400 TITLE_REQUIRED` — пустой title; `404 SESSION_NOT_FOUND`.

## DELETE /v1/api/sessions/{id}

Soft-delete диалога (v0.31.0, T-000122): помечает `deleted_at`, диалог исчезает из всех user-эндпоинтов (`GET /v1/api/sessions`, `batch`, `messages`, `query-versions`, `PATCH rename`). Данные (сообщения, версии запроса, usage) остаются в БД.

**Response (200):** `{ "serviceInfo": {"success": true} }`

**Errors:** `400 MISSING_ID` — пустой `{id}`; `404 SESSION_NOT_FOUND` — не существует / чужая / уже удалена (неразличимо: idempotent + не течёт факт существования чужих сессий).

**Side effects:**
- `sessions.deleted_at = NOW()`; сессия выселяется из in-memory кэша сервера (иначе `/chat` воскресил бы её из памяти; `hydrateFromDB` дополнительно фильтрует `deleted_at IS NULL` против воскрешения после рестарта).
- `/chat` с удалённым `sessionId` ведёт себя как с несуществующим.

## POST /v1/api/sessions/{id}/compact

Ручная компактизация контекста диалога (R1, v0.68.0, T-000269). Сжимает старую историю в rolling-summary + durable-ссылки. Неразрушающе: `messages` не трогаются. Rolling: обновляет существующий checkpoint. Floor-guard (D8): если projected fill < `compaction.floor_pct` × limit → `409 COMPACTION_BELOW_FLOOR`. Auth: user + product (ownership check). С `v0.70.3` manual-путь проходит через те же safety-механизмы, что авто-триггер: per-session guard (не интерливится с авто-компактизацией) и buffer-gate (не компактим при незакоммиченном буфере).

**Биллинг (`v0.74.0`).** Summarizer-вызов компактизации **списывается с баланса пользователя** как обычный AI-вызов — по цене модели компактизатора (см. приоритет выбора модели ниже; дешёвый `defaultSummarizerModel` продукта уменьшает стоимость). Это касается и ручной компактизации, и авто-триггера на границе хода. Списание видно в usage пользователя; при нехватке средств авто-компактизация тихо пропускается (чат продолжает работать с несжатым контекстом), ручная возвращает `402 INSUFFICIENT_BALANCE`.

**Request (optional body):**
```json
{
  "model": "google/gemini-3.5-flash"
}
```
`model` — опциональный override модели компактизатора. Приоритет: `body.model` > `product.defaultSummarizerModel` > `session.model`. Пустое тело = default.

**Response (200):**
```json
{
  "serviceInfo": {"success": true},
  "sessionId": "uuid",
  "summary": "сжатая сводка диалога",
  "upToMessageId": "42",
  "referencesCount": 3,
  "model": "google/gemini-3.5-flash",
  "trigger": "manual",
  "tokensBefore": 152000,
  "tokensAfterEstimate": 46000,
  "contextFill": {"model": "anthropic/claude-sonnet-4", "lastPromptTokens": 46000, "contextWindow": 200000, "fillRatio": 0.23, "compactionThresholdTokens": 141666, "thresholdRatio": 0.7083}
}
```
`model` — фактически использованная модель компактизатора. **REQ-053 (`v0.75.0`):** `trigger` (для этой ручки всегда `manual`), `tokensBefore` — занятость на момент запуска, `tokensAfterEstimate` — **оценка** занятости после сжатия (честный замер появится на следующем AI-ходе), `contextFill` — обновлённая телеметрия от этой оценки по модели **сессии** (см. `client-api-chat.md`; порог — от неё же). Токен-поля/`contextFill` опущены, если занятость на старте не была измерима.

**Errors:**
- `503 COMPACTION_NOT_CONFIGURED` — компактизация не настроена (Compactor=nil)
- `400 MISSING_ID` — пустой `{id}`
- `404 SESSION_NOT_FOUND` — не существует / чужая
- `409 COMPACTION_DISABLED` — `compactionEnabled=false` для продукта
- `409 COMPACTION_BELOW_FLOOR` — контекст слишком мал (ниже floor_pct × limit)
- `409 COMPACTION_NOTHING_TO_COMPACT` — слишком мало сообщений (< 2 обменов)
- `409 COMPACTION_BUFFER_PENDING` (`v0.70.3`) — есть незакоммиченный буфер (ожидается `apply_query_change`) — компактизация отложена, повторите после коммита буфера
- `409 COMPACTION_NOT_READY` (`v0.70.3`) — сообщения хода ещё сохраняются (транзиентно) — повторите через секунду
- `402 INSUFFICIENT_BALANCE` (`v0.74.0`) — недостаточно средств на summarizer-вызов
- `503 BILLING_NOT_CONFIGURED` (`v0.74.0`) — биллинг недоступен, резерв взять нельзя
- `500 BILLING_ERROR` (`v0.74.0`) — прочая ошибка резерва баланса
- `500 COMPACTION_FAILED` — ошибка LLM-вызова или persist

## POST /v1/api/sessions/{id}/compact/async

**Status: implemented (v0.75.0, T-000294).** Async-вариант ручной компактизации — **канонический для 1С-клиентов**: sync-ручка держит соединение весь summarizer-вызов (таймаут-риск), async возвращает тикет сразу. Семантика ядра идентична sync (те же гарды, guard, биллинг, checkpoint-рендер).

**Request:** то же опциональное тело (`model` — override компактизатора). Синхронно проверяются только вход и ownership: `503 COMPACTION_NOT_CONFIGURED`, `400 MISSING_ID`, `404 SESSION_NOT_FOUND`. Все прочие исходы (buffer-gate, floor, биллинг, LLM) приходят статусом тикета.

**Response 202:**
```json
{"serviceInfo": {"success": true}, "ticketId": "hex-32", "sessionId": "uuid", "status": "pending"}
```

## GET /v1/api/sessions/{id}/compact/async/{ticketId}

Поллинг тикета компактизации. `status`:
- `pending` — идёт summarizer-вызов, поллить дальше (интервал 1–2 с разумен).
- `completed` — `result` = **точный ответ sync-ручки** (summary, `trigger`/`tokensBefore`/`tokensAfterEstimate`/`contextFill` — см. выше).
- `failed` — `errorCode`/`errorMessage` = код, который sync-ручка отдала бы HTTP-ошибкой (`COMPACTION_DISABLED`, `COMPACTION_BELOW_FLOOR`, `COMPACTION_BUFFER_PENDING`, `COMPACTION_NOT_READY`, `INSUFFICIENT_BALANCE`, `BILLING_NOT_CONFIGURED`, `COMPACTION_FAILED`, …); `serviceInfo.success=false`.

**Тикеты in-memory** (не БД): живут ~15 минут и не переживают рестарт сервера — `404 TICKET_NOT_FOUND` на неизвестном/истёкшем тикете означает «повторите компактизацию» (безопасно: per-session guard не даст задвоить проход, повторный запуск над уже сжатой сессией отработает штатно или отдаст floor). Терминальный тикет читается повторно (идемпотентный poll) до истечения TTL.

## GET /v1/api/sessions/{id}/query-versions

Список версий текста запроса (для пикера отката). Лёгкая проекция без полного текста и diff.

**Response (200):**
```json
{
  "serviceInfo": {"success": true},
  "versions": [
    {"id": "uuid", "author": "user|agent", "origin": "client|server", "hunkCount": 3, "createdAt": "RFC3339",
     "valid": true, "skdValid": false, "validationError": "..."}
  ]
}
```
Версии в хронологическом порядке (объединённая хронология обоих потоков). `hunkCount` — число изменённых фрагментов относительно предыдущей версии (0 для первой). `origin` (C3) — происхождение версии: `client` (правка человека) / `server` (правка агента); `author` сохранён как legacy-поле (вычислен из origin). `valid`/`skdValid`/`validationError` (v0.61.1, T-000245) — валидность версии по проверке консоли, omitempty: присутствуют ⟺ консоль сообщала (`queryValidation`/apply-payload). **Errors:** `404 SESSION_NOT_FOUND`.

## GET /v1/api/sessions/{id}/query-versions/{versionId}

Полный текст версии N (для отката редактора на клиенте).

**Response (200):**
```json
{
  "serviceInfo": {"success": true},
  "id": "uuid", "author": "user|agent", "origin": "client|server",
  "queryText": "<полный текст версии>", "createdAt": "RFC3339"
}
```
Откат = выставить `queryText` в редактор; следующий репорт зафиксирует возврат как новую версию (`origin=client`). Сервер откат отдельно не применяет. `origin` (C3) — происхождение версии; `author` сохранён как legacy (вычислен из origin). **Errors:** `404 SESSION_NOT_FOUND`, `404 VERSION_NOT_FOUND`.

---

# Public API — Обновления клиента (требует `swan_uk_*` + `X-Product-Key: swan_pk_*`)

С v0.32.0 (F-000016) сервер проксирует микросервис SwanUpdates (pull-only): клиент узнаёт о новой версии своего дистрибутива и скачивает бинарь **через сервер** — напрямую в SwanUpdates клиенты не ходят. Продукт определяется server-side по `X-Product-Key`, а имя продукта в SwanUpdates берётся из атрибута продукта `updates_product_name` (`products`, миграция 032; правится через `PATCH /v1/admin/products/{id}`) — клиент своё имя продукта в запросе не передаёт. Пустой `updates_product_name` → `404 UPDATES_NOT_AVAILABLE`. Оба эндпоинта под общим rate-limit (см. RATE_LIMIT_EXCEEDED ниже).

## GET /v1/api/updates/check

Проверка наличия обновления для продукта клиента.

**Query:** `platform` (string, **optional** — литеральный токен платформы, согласованный с публикацией релизов, напр. `windows-x64`; платформу выбирает клиент скачивания. Без него upstream отдаёт последний релиз продукта across-platforms); `currentVersion` (string, optional — без него `updateAvailable` всегда `true`).

**Response (200):**
```json
{
  "serviceInfo": {"success": true},
  "latestVersion": "v1.10.0",
  "updateAvailable": true,
  "changelog": "...",
  "fileName": "console.epf",
  "fileSize": 12345,
  "checksumSha256": "<64 hex>"
}
```
- `updateAvailable` — есть ли обновление. **С v0.60.0 считается сервером локально** (semver: `latestVersion` новее `currentVersion`; ведущий `v` и pre-release-суффикс отбрасываются, `v1.10.0 > v1.9.0` — числовое сравнение, не строковое). Без `currentVersion` → `true`; пустой `latestVersion` → `false`. Раньше значение бралось из upstream; теперь выводится у нас, т.к. ответ throttle-кешируется без привязки к версии клиента (см. ниже). Поле `comparison` (`newer|equal|older`) **снято с v0.46.0** (T-000181). `changelog` опционален.
- **Throttle (v0.60.0):** результат check по `(product, platform)` кешируется на ~60с (±20% jitter) — частый поллинг внутри окна обслуживается из кеша, не бьёт SwanUpdates на каждый запрос. `latestVersion`/`changelog`/`fileName`/`fileSize`/`checksumSha256` могут быть до ~60с устаревшими; `updateAvailable` при этом всегда пересчитывается под `currentVersion` конкретного запроса (кеш version-independent). Смена апдейт-канала в админке (`PUT /v1/admin/config`) сбрасывает кеш немедленно.
- Идентификаторы релиза (`releaseId`/`downloadPath`) клиенту намеренно не отдаются — скачивание идёт фиксированным эндпоинтом ниже, сервер сам резолвит последний релиз.

**Errors:**
- `UPDATES_NOT_AVAILABLE` (404) — для продукта запроса апдейт-канал не настроен
- `RELEASE_NOT_FOUND` (404) — релизов под (продукт[, platform]) нет
- `RATE_LIMIT_EXCEEDED` (429) — общий лимит update-эндпоинтов (30 запросов / 5 минут на пользователя)
- `RATE_LIMITED` (429) — лимит на стороне SwanUpdates; заголовок `Retry-After` пробрасывается
- `UPDATES_UPSTREAM_ERROR` (502) — SwanUpdates недоступен или вернул ошибку
- `DB_ERROR` (500) — ошибка БД при резолве продукта

## GET /v1/api/updates/download

Скачивание последнего релиза для продукта клиента. **Успех — бинарь, не JSON.**

**Query:** `platform` (string, **optional** — тот же токен, что в check; без него — последний релиз продукта across-platforms).

**Response (200 | 206):** тело — содержимое файла релиза. Заголовки: `Content-Type`, `Content-Length`, `Content-Disposition` (`attachment; filename="..."`), `X-Checksum-Sha256`, `ETag`, `Accept-Ranges`, `Content-Range` (при 206).

- Request-заголовки `Range` / `If-Range` пробрасываются — докачка поддержана (206). `If-None-Match` не пробрасывается — `304` на этом эндпоинте не возникает.
- **Клиент обязан сверить sha256** скачанного файла с заголовком `X-Checksum-Sha256`; при несовпадении (обрыв) — повторить скачивание (можно с `Range`). Сверка гарантирует целостность, не аутентичность (подписи в цепочке нет — доверие на TLS + S2S-ключах портфеля).
- Между check и download мог выйти новый релиз (или старый мог быть удалён): download всегда отдаёт текущий последний; checksum для сверки — из заголовка ответа download, не из устаревшего check. Если релиз исчез между внутренним резолвом и скачиванием — `RELEASE_NOT_FOUND`, клиент повторяет цикл check+download.

**Errors:** (JSON-envelope) — те же коды, что у check. `RATE_LIMIT_EXCEEDED` (429) на download дополнительно возвращается при превышении глобального лимита одновременных скачиваний (8 стримов на весь сервер) — повторить позже.

**Side effects:** SwanUpdates пишет факт скачивания в свой `download_log` (server identity = наш сервер).

---

