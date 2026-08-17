# Admin API — Users, sessions & prompt writes

> Часть admin-контракта. Индекс: [admin-api.md](admin-api.md).

## POST /v1/admin/users

Создание пользователя без identity-привязки (admin issues a "blank" user). Identity появится позже через
`POST /v1/admin/users/{id}/keys` (выпуск user-ключа) или OTP-логин (через привязку `infraconnect_id`).
Тело запроса игнорируется.

**Response 201:**
```json
{
  "serviceInfo": {"success": true},
  "user": {
    "id": "550e8400-...",
    "infraconnectId": "uuid, opt — пусто для admin-issued users до их OTP-логина",
    "displayName": "string — кеш имени из InfraConnect; '' для admin-issued или пока IC не доставит",
    "balanceCoins": 0
  }
}
```

---

## GET /v1/admin/users

Список пользователей с пагинацией. Query: `page` (default 1), `size` (default 50, max 100).

> С v0.41.0 (F-000012) `balanceCoins` — read-proxy из Swan_Billing (per-user, soft-fail→0); `balanceReservedCoins` всегда `0` (холды на стороне Billing).

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "users": [
    {"id": "...", "infraconnectId": "uuid|omitempty", "displayName": "string — '' если нет данных (REQ-020)", "balanceCoins": 20000, "balanceReservedCoins": 0, "createdAt": "...", "updatedAt": "..."}
  ],
  "total": 5,
  "page": 1,
  "size": 50
}
```

---

## GET /v1/admin/users/{id}

Детали одного пользователя (включая soft-deleted для аудита).

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "user": {"id": "...", "infraconnectId": "uuid|omitempty", "displayName": "string — '' если нет данных", "balanceCoins": 20000, "balanceReservedCoins": 0, "createdAt": "...", "updatedAt": "...", "deletedAt": ""}
}
```

---

## DELETE /v1/admin/users/{id}

Soft delete пользователя (проставляет `deleted_at`). Все user-ключи юзера автоматически считаются revoked (auth по ним → 401 USER_DELETED).

**Query-параметры:**
- `hard` — `true|false` (опц., default `false`). При `true` (F-000029) строка `users` **физически удаляется**: каскадит `user_keys` (swan_uk_ ключи исчезают), `usage_stats.user_id`/`user_auth_events.user_id` → NULL. **Сессии и сообщения сохраняются** — у `sessions` нет FK на `users` (диалоги остаются в БД, orphaned by `user_id`). Используется для ретайра legacy-аккаунтов при переходе на InfraConnect per-user ключи.

**Response 200:**
```json
{"serviceInfo": {"success": true}}
```

---

## POST /v1/admin/users/backfill-display-names

Разовый бэкфилл `display_name` из InfraConnect (T-000299, REQ-050, v0.77.0): у пользователей, не перелогинившихся после v0.75.0 (REQ-043), метка пуста — админ-поверхности показывают uuid. Ручка тянет identity по `infraconnect_id` через S2S bulk lookup платформы и заполняет **только пустые** `display_name`; выбор метки — `name` → `email` → `phone` (как онлайн-ветка REQ-043).

Без тела. **Идемпотентна**: повторный вызов на неизменной базе → `filled: 0`; заполненные вручную/из IC метки не перетираются; гонка с параллельным OTP-логином безопасна (логин выигрывает, строка идёт в `skippedRace`).

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "candidates": "int — юзеров с пустой меткой и живым infraconnect_id на старте",
  "filled": "int — меток заполнено этим вызовом",
  "notFoundInIc": "int — identity не найдена на платформе",
  "noLabel": "int — юзер на платформе есть, но name/email/phone пусты",
  "skippedRace": "int, omitempty — метку успел заполнить параллельный логин",
  "lookupErrors": "int, omitempty — S2S/DB-сбоев; повтор вызова доберёт остаток"
}
```

**Errors:**
- `INFRACONNECT_NOT_CONFIGURED` (503) — S2S-клиент платформы не настроен.

---

## ~~PATCH /v1/admin/users/{id}/balance~~ (removed в v0.41.0)

**Снят** (F-000012, контракт-слом). Баланс юзеров вынесен в Swan_Billing — правка баланса только через его admin-поверхность (у Billing `credit`/`charge`, абсолютного «set» нет). Запрос на этот путь теперь `404`. Просмотр баланса — read-proxy в `GET /v1/admin/users` / `GET /v1/admin/users/{id}` (поле `balanceCoins` из Billing; `balanceReservedCoins` всегда `0`).

---

## GET /v1/admin/sessions

Список чат-сессий с пагинацией. Query: `page` (default 1), `size` (default 50, max 100), `includeDeleted` (bool, default `false`), `sortBy`, `sortDir`.

Сортировка (v0.52.0): `sortBy=lastMessageAt|createdAt|updatedAt|messageCount|model|userDisplayName`, `sortDir=asc|desc`; default `lastMessageAt desc`. Для `GET /v1/admin/users/{id}/sessions` доступны те же поля, кроме `userDisplayName`. Некорректная сортировка возвращает `400 INVALID_REQUEST`.

По умолчанию soft-deleted сессии (`deleted_at IS NOT NULL`) скрыты; `?includeDeleted=true` включает их в выдачу (аудит). Тот же флаг применим к `GET /v1/admin/users/{id}/sessions` и `GET /v1/admin/sessions/{id}/messages`.

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "sessions": [
    {
      "id": "a1b2c3d4-...",
      "model": "anthropic/claude-sonnet-4",
      "userId": "uuid",
      "userDisplayName": "Ivan Petrov",
      "systemPrompt": "...",
      "temperature": 0.7,
      "messageCount": 5,
      "checkpointCount": 2,
      "createdAt": "2026-04-10T12:00:00Z",
      "updatedAt": "2026-04-10T12:05:00Z",
      "lastMessageAt": "2026-04-10T12:05:00Z",
      "economics": {
        "chargedCoins": 20000, "chargedUsd": 40, "chargedRub": 4000,
        "routerCostUsd": 20, "routerCostRub": 2000,
        "marginUsd": 20, "marginRub": 2000
      }
    }
  ],
  "total": 42,
  "page": 1,
  "size": 50
}
```

`userId` / `userDisplayName` (v0.49.0, T-000183) приходят из `users`; fallback для display-name: `display_name` → `infraconnect_id` → `user_id` → отсутствие поля. **Наполнение `display_name` (REQ-043, `v0.75.0`):** InfraConnect пока не доставляет имя в OTP-verify, а `infraconnect_id` — сам uuid (поэтому «владелец = uuid» и был нормой). С v0.75.0 при OTP-verify пустой `display_name` заполняется **identity логина** (нормализованный email/телефон) — человекочитаемая метка появляется при первом же логине пользователя; IC-имя, когда платформа начнёт его отдавать, перезапишет identity-метку.

**`checkpointCount`** (REQ-046, `v0.74.0`) — число проходов компактизации контекста сессии (`chat_session_checkpoints.pass_count`; rolling-checkpoint один, счётчик инкрементится каждым проходом). `0` = компактизаций не было. Поле приходит всегда (без `omitempty`). То же поле — в `GET /v1/admin/users/{id}/sessions`. Для сессий, компактизированных только до v0.74.0, значение `1` (стартовое по миграции; точное число ранних проходов не восстановимо).

**`economics` (T-000210, админ-маржа, опционально):** агрегат экономики сессии — `chargedCoins` (списано с клиента, с наценкой) + `chargedUsd`/`chargedRub` (то же по **текущему** курсу `pricing.coins_per_usd`/`coins_per_rub`), `routerCostUsd`/`routerCostRub` (реальная закупка у Router'а, COGS) и `marginUsd`/`marginRub` = списание − закупка. Опускается (`omitempty`), если по сессии нет оплаченных ходов. **Только админ** — `routerCost*` (COGS) клиенту не отдаётся. Конвертация списания в $/₽ идёт по текущему курсу: если курс менялся с момента ходов, исторические значения приблизительны (live-вью). Те же поля — per-ход в `messages[].economics` (`GET /v1/admin/sessions/{id}/messages`) и в `GET /v1/admin/users/{id}/sessions`.

---

## GET /v1/admin/sessions/{id}/messages

Метаданные сессии + сообщения (включая `visible=false`). T-000060/REQ-022 в v0.20.2: фильтр `visible = TRUE` убран — админ-консоль диагностический инструмент и должна видеть полный tool-flow (assistant_tool_use, tool_result, любые системные служебные сообщения). Без query-параметров отдаётся старое поведение — **все** сообщения; с `limit`/`before` включается cursor-пагинация (v0.49.0, T-000182).

**Query (v0.49.0/v0.52.0):** `?limit=100&before=<messageId>&order=desc` — `limit` default 100, max 200; `before` выбирает сообщения с `id < before`. `order=desc|asc`, default `desc` (latest-first). Для следующей страницы в `desc`-режиме берите `before` равным последнему `id` текущей страницы; для `asc`-режима — первым `id` текущей страницы. Если `limit`/`before` не переданы, `hasMore=false` и возвращаются все сообщения в выбранном порядке.

**`?includeDeleted=true`** (v0.31.0): по умолчанию soft-deleted сессия → `404 SESSION_NOT_FOUND`; с флагом метаданные+сообщения отдаются (аудит).

**`session.systemPrompt`** (REQ-024 v0.23.2): полный текст системного промпта сессии (читается из `sessions.system_prompt`). Поле приходит всегда — пустая строка `""` для сессий, где промпт не был задан (без `omitempty`). Симметрично с листингом `/v1/admin/sessions`.

**`session.economics` + `messages[].economics`** (T-000210, админ-маржа): `session.economics` — агрегат по сессии, `messages[].economics` — маржа отдельного хода (присутствует у assistant-ходов с usage-строкой; опускается у user/tool/system-сообщений). Структура `TurnEconomics` — см. `GET /v1/admin/sessions`. Router-COGS (`routerCost*`) — только админу.

**`session.contextFill`** (T-000230, `v0.72.0`) — наполнение контекстного окна: `{"model": "z-ai/glm-5.2", "lastPromptTokens": 50000, "contextWindow": 200000, "fillRatio": 0.25}`. `lastPromptTokens` — `prompt_tokens` **последнего измеренного AI-вызова** сессии (= полный отправленный контекст: system prompt + tools + история); `contextWindow` — окно модели этого вызова из Router-каталога; `fillRatio = lastPromptTokens / contextWindow`. Может быть >1 (устаревший каталог / модель без жёсткой отсечки) — клиент клампит сам. Опускается целиком, если в сессии нет измеренных ходов или окно модели каталогу неизвестно.

**`messages[].content` для `kind=assistant_tool_use`:** если AI вернул text-блок рядом с tool_use (типичный шаблон Anthropic: «*Сейчас прочитаю X*» + tool_use в одном ответе) — `content` несёт этот текст. Если модель не вернула text-блок — `content=""`. Сохранение text+tool_use end-to-end покрыто тестами `provider.TestClaudeResponseDecode_PreservesTextAlongsideToolUse` / `TestOpenAIResponseDecode_PreservesContentAlongsideToolCalls`. Если в конкретной сессии `content=""` — модель в этот раз не дала комментарий, серверной потери нет.

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "session": {
    "id": "a1b2c3d4-...",
    "model": "anthropic/claude-sonnet-4",
    "userId": "uuid",
    "userDisplayName": "Ivan Petrov",
    "systemPrompt": "Ты помощник по написанию запросов на языке запросов 1С (1QL)...",
    "temperature": 0.7,
    "createdAt": "...",
    "updatedAt": "..."
  },
  "messages": [
    {
      "id": 1,
      "role": "user",
      "kind": "user_text",
      "status": "completed",
      "visible": true,
      "content": "Проанализируй код...",
      "createdAt": "..."
    },
    {
      "id": 2,
      "role": "assistant",
      "kind": "assistant_tool_use",
      "status": "completed",
      "visible": false,
      "content": "",
      "model": "anthropic/claude-sonnet-4.6",
      "temperature": 0.7,
      "realModel": "anthropic/claude-sonnet-4.6-20260620",
      "reasoningText": "Пользователь просит заменить блок — вижу, что...",
      "reasoningEffort": "medium",
      "promptTokens": 5240,
      "completionTokens": 312,
      "totalTokens": 5552,
      "toolUses": [
        {"id": "toolu_abc", "name": "apply_code_change", "input": {"file": "...", "operation": "replace", "search": "...", "replace": "..."}, "executor": "client"},
        {"id": "toolu_def", "name": "load_skill", "input": {"name": "buh_skill"}, "executor": "server"}
      ],
      "createdAt": "..."
    },
    {
      "id": 3,
      "role": "user",
      "kind": "tool_result",
      "status": "completed",
      "visible": false,
      "content": "",
      "toolResults": [
        {"toolUseId": "toolu_abc", "isError": false, "content": "Файл изменён успешно"}
      ],
      "createdAt": "..."
    }
  ],
  "hasMore": false
}
```

**Расширения DTO в v0.20.2 (T-000060/REQ-022):**
- `kind` (string) — domain-уровневый тип сообщения: `user_text` / `assistant_text` / `assistant_tool_use` / `tool_result` / `system`. Дискриминатор payload-полей. Удобен админу для диф-рендеринга (иконки, тоны, expandable raw).
- `status` (string, v0.52.0) — lifecycle сообщения: `completed`, `pending`, `failed`, `interrupted`. В текущем sync-path новые сообщения обычно `completed`; поле добавлено для корректной диагностики async/interrupt/failure-сценариев.
- `visible` (bool) — отражает `messages.visible` колонку. `false` = служебное сообщение, в клиентский view (chat-history) не попадает.
- `toolUses` (`[]ToolUseBlock`, omitempty) — сырой payload из `messages.tool_uses` jsonb. Поля: `id` (string, для связки с tool_result), `name` (string, имя тула), `input` (any, аргументы тула), `executor` (string, REQ-042 — см. ниже).
- `toolResults` (`[]ToolResult`, omitempty) — сырой payload из `messages.tool_results` jsonb. Поля: `toolUseId` (string), `isError` (bool), `content` (string).
- `hasMore` (bool, v0.49.0) — есть более старые сообщения для следующей cursor-страницы. Для legacy-запроса без `limit`/`before` всегда `false`.

**Паритет с клиентской ручкой (REQ-042, v0.66.1):** админ-ручка теперь отдаёт те же диагностические поля, что и клиентская `GET /v1/api/sessions/{id}/messages`:
- `reasoningText` (string, omitempty) / `reasoningEffort` (string, omitempty — `none|minimal|low|medium|high`) — размышления assistant-хода; присутствуют только там, где reasoning был.
- `promptTokens` / `completionTokens` / `totalTokens` (int, omitempty) — токены AI-вызова, породившего assistant-ход (JOIN `usage_stats` по `message_id`). Присутствуют ⟺ есть usage-строка; omit = «нет данных», не ноль.
- `cacheReadTokens` / `cacheWriteTokens` (int, omitempty, T-000286 `v0.73.0`) — cache-aware usage AI-вызова (prompt caching, REQ-013): токены, прочитанные из / записанные в provider-кэш; оба ⊆ `promptTokens`. Присутствуют ⟺ >0 (кэш реально сработал); сообщения до v0.73.0 / не-кэшируемые маршруты поля не отдают.
- `toolUses[].executor` (string) — **исполнитель тула**: `server` (сервер исполняет сам — `load_skill`, `get_latest_query_version`; реестр `toolset.IsServerTool`) или `client` (бунсится в 1С — `apply_*`, `validate_query`, `execute_command` и пр.). Даёт админке однозначного исполнителя без угадывания по имени. `tool_result` связывается с `tool_use` по `toolUseId` → исполнитель капсулы = `executor` её `toolUse`.

**Observability синтетической `role=checkpoint` строки (REQ-046, `v0.74.0`, T-000291):** синтетика несёт те же учётные поля, что assistant-ходы — `model` (запрошенная модель компактизатора), `realModel` (фактическая, если отличалась), `promptTokens`/`completionTokens`/`totalTokens` и `economics` (структура `TurnEconomics`; `chargedCoins` > 0 — компактизация биллится юзеру с v0.74.0, `routerCost*` — COGS summarizer-вызова). Поля присутствуют ⟺ есть снапшот прохода: checkpoint'ы, чей последний проход был **до** v0.74.0, отдают синтетику без этих полей (omit = «нет данных»). Rolling-семантика: наблюдается только **последний** проход (checkpoint один на сессию, каждый проход перезаписывает снапшот); `checkpointCount` в списках сессий даёт число проходов. `temperature` и `status` у компактизации не существуют — не отдаются.

**Checkpoint-рендер синтетики (F-000037/REQ-053, `v0.75.0`):** дополнительно `checkpointTrigger` (`auto` — авто-триггер границы хода | `manual` — ручная компактизация; проходы до v0.75.0 отдают `auto` как приближение), `tokensBefore` (projected-занятость контекста на момент триггера), `tokensAfterEstimate` (**оценка** занятости после сжатия — summary + хвост; не замер) и `checkpointPass` (= `pass_count` — стабильный идентификатор прохода внутри сессии). Токен-поля `omitempty` — опущены у проходов до v0.75.0. Те же поля — в клиентской синтетике `GET /v1/api/sessions/{id}/messages`.

**Orphan `tool_result` при пагинации:** компактизация неразрушающая — admin-ручка всегда отдаёт полную персистнутую историю, checkpoint-граница сообщений не скрывает. Поэтому `tool_result`, чей парный `tool_use` отсутствует в том же ответе, возможен только при **cursor-пагинации** (пара осталась на соседней странице). Клиент связывает пары по `toolUseId` в пределах загруженного таймлайна; для непарного результата — подгрузить соседнюю страницу.

**Ошибки:** `404 SESSION_NOT_FOUND`

---

## GET /v1/admin/sessions/{id}/query-versions

История версий текста запроса сессии с полными структурными диффами (T-000101, admin-read). Для рендера «истории запроса» поверх таймлайна сессии.

**Response (200):**
```json
{
  "serviceInfo": {"success": true},
  "versions": [
    {
      "id": "uuid",
      "author": "user|agent",
      "origin": "client|server",
      "versionHash": "sha256-hex",
      "diff": [{"old": "...", "new": "...", "lineRange": [start, end]}],
      "queryText": "ВЫБРАТЬ 1 ИЗ Справочник.Товары",
      "createdAt": "RFC3339",
      "valid": true,
      "skdValid": false,
      "validationError": "..."
    }
  ]
}
```
- Версии в хронологическом порядке (старые → новые), объединённая хронология обоих потоков (server+client).
- `diff` — массив структурных hunks (изменения относительно предыдущей версии). Для первой версии сессии — `[]`.
- `queryText` (v0.56.2, REQ-036) — полный текст ИМЕННО этой версии. Приходит для всех версий, включая первую (где `diff: []`) — чтобы админ видел исходный запрос, а не только бейдж. Симметрично user-side `GET /v1/api/sessions/{id}/query-versions/{versionId}`. С v0.61.1 (T-000244) `queryText` может быть пустым — версия «пустой запрос» (пустой редактор — тоже состояние).
- `origin` (C3) — происхождение версии = таблица-источник: `server` (правка агента) / `client` (правка человека). Без угадывания автора.
- `author` — legacy-поле, вычислено из `origin` (`server→agent`, `client→user`); сохранено для обратной совместимости.
- `valid`/`skdValid`/`validationError` (v0.61.1, T-000245) — валидность версии по проверке консоли (обычная + СКД), omitempty: присутствуют ⟺ консоль сообщала. Для server-версий `accepted` отражает ФАКТ применения в редактор, `valid` — свойство текста; «applied=true + valid=false» — легальная комбинация («применён, но невалиден»).
- Пустой список — норма для сессий, где консоль не слала `queryText` (ключ отсутствовал в запросах).

---

## GET /v1/admin/sessions/{id}/references

Durable-ссылки компактизации контекста сессии (R2, v0.70.0, T-000280). Admin-инспекция того, что суммаризатор счёл важным при сжатии контекста — идентификаторы, решения, требования, сохранённые дословно (snapshot body, D4 спеки R1). Ссылки пишутся при компактизации (R1, `POST /compact` или авто-триггер); этот endpoint их читает.

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "references": [
    {
      "id": "uuid",
      "name": "account_id",
      "description": "Код счёта из ТЗ",
      "body": "41.01",
      "kind": "note",
      "bodyTokens": 5,
      "createdAt": "2026-07-10T12:00:00Z"
    }
  ]
}
```

- `references` — массив `ReferenceDTO`. Пусто → `[]` (не `null`, JSON-контракт).
- `name` — короткая метка (≤200 символов), уникальна per-session case-insensitive (dedup R1 по `lower(name)`).
- `description` — одна строка зачем это (может быть пустой).
- `body` — точный текст сохранённого факта (snapshot, переживает edit/delete исходного сообщения).
- `kind` — `message` (дословная выжимка из сообщения) | `note` (производный факт) | `file` (forward-compatible, в R1/R2 не пишется).
- `bodyTokens` — оценка размера (от суммаризатора).
- `createdAt` — когда извлечена при компактизации.
- Не пагинируется (cap `compaction.max_references`=5 за проход компактизации, реальный максимум — N проходов × 5).
- Если компактизация отключена (`compactionRepo == nil`, dev / R1 не поднят) → `200` + `references: []` (не `503` — admin может инспектировать сессию без compaction).

**Ошибки:** `400 MISSING_ID` (нет `{id}`), `500 DB_ERROR` (ошибка чтения).

**Связанные endpoint'ы:** `GET /v1/admin/sessions/{id}/messages` — синтетическая `role=checkpoint`-строка (`id=0`, не персистится) показывает что контекст был сжат (R1, T-000268). С `v0.70.3` строка появляется **только на странице, содержащей границу компактизации** (есть и покрытое summary сообщение, и tail-сообщение) — в т.ч. в DESC-порядке позиция хронологически корректна, на прочих страницах строка не дублируется. `POST /v1/api/sessions/{id}/compact` — ручная компактизация (R1, T-000269, user-auth; с v0.70.3 — через per-session guard + buffer-gate).

---

## GET /v1/admin/sessions/{id}/summary

Предагрегированный итог по сессии (T-000308 `v0.79.0`, REQ-055). Σ по **всем AI-вызовам** сессии (`usage_stats` по `session_id` — та же семантика, что в отчётном агрегате `admin-api-reports.md` и в `economics` списков: retry-вызовы и компактизация включены). Закрывает капкан клиентского суммирования: `messages` пагинируются, сумма по загруженным страницам занижает длинные диалоги (тот же мотив, что `checkpointCount` — T-000293).

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "sessionId": "uuid",
  "summary": {
    "messageCount": 42,
    "checkpointCount": 2,
    "promptTokens": 480000,
    "completionTokens": 36000,
    "totalTokens": 516000,
    "cacheReadTokens": 310000,
    "cacheWriteTokens": 52000,
    "economics": {"chargedCoins": 120.5, "chargedUsd": 0.27, "chargedRub": 24.1, "routerCostUsd": 0.11, "routerCostRub": 9.9, "marginUsd": 0.16, "marginRub": 14.2}
  }
}
```

- `messageCount` / `checkpointCount` — без omitempty, 0 значимый (сообщений нет / компактизаций не было).
- `promptTokens` / `completionTokens` / `totalTokens` — omitempty, присутствуют ⟺ по сессии есть хотя бы одна usage-строка (omit = «нет измеренных вызовов», не ноль).
- `cacheReadTokens` / `cacheWriteTokens` — Σ по AI-вызовам, оба **⊆ `promptTokens`** (кэш — подмножество входа, не слагаемое сверх него; та же семантика, что в отчётах). omitempty: присутствуют ⟺ Σ>0 — тот же контракт присутствия, что у per-message `cacheReadTokens`/`cacheWriteTokens` в `/messages`: opaque-провайдер кэш-полей не сообщает (в метеринге нули) → полей нет, «кэш не наблюдался» ≠ «0 из кэша». Доля кэша = `cacheReadTokens ÷ promptTokens`.
- `economics` — агрегат маржи по сессии (структура `TurnEconomics`, как в списке сессий); nil при отсутствии денежных данных.

**Ошибки:** `400 MISSING_ID`, `404 SESSION_NOT_FOUND`, `500 DB_ERROR`.

**Списки сессий:** `GET /v1/admin/sessions` и `GET /v1/admin/users/{id}/sessions` отдают те же `cacheReadTokens` / `cacheWriteTokens` на каждом элементе (тот же контракт присутствия ⟺ Σ>0) — колонка/сортировка «где кэш не сработал» по всему парку без per-session запросов.

---

## GET /v1/admin/tables

Список таблиц в public schema с количеством строк и размерами на диске.


**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "databaseBytes": 268435456,
  "tables": [
    {"name": "error_logs", "rowCount": 12, "totalBytes": 65536, "heapBytes": 16384, "toastBytes": 8192, "indexBytes": 32768},
    {"name": "messages", "rowCount": 1234, "totalBytes": 104857600, "heapBytes": 8388608, "toastBytes": 88080384, "indexBytes": 4194304},
    {"name": "users", "rowCount": 5, "totalBytes": 49152, "heapBytes": 8192, "toastBytes": 0, "indexBytes": 32768}
  ]
}
```

**`rowCount` — ОЦЕНКА, размеры — ФАКТ (T-000318, REQ-056).** `rowCount` — приближённое значение планировщика из `pg_class.reltuples` (после массовых INSERT/DELETE расходится с реальным до следующего `VACUUM`/`ANALYZE`; точное — в `GET /admin/tables/{name}` через `COUNT(*)`); клиенту стоит помечать его как оценку. Размеры — фактические байты из системного каталога (`pg_*_size`, без сканирования таблиц, стоимость ~0):

- `totalBytes` — `pg_total_relation_size`: куча + TOAST + все индексы;
- `heapBytes` — основная куча (main fork), без TOAST и индексов;
- `toastBytes` — TOAST-хранилище (включая его индекс); `0`, если TOAST'а у таблицы нет. Длинные тексты (`messages`, `chat_async_turns`) лежат в основном здесь;
- `indexBytes` — `pg_indexes_size`: все индексы таблицы;
- инвариант `totalBytes ≈ heapBytes + toastBytes + indexBytes` — расхождение только на служебные FSM/VM-страницы (не подгоняется вычитанием);
- `databaseBytes` — `pg_database_size` всей БД: для доли таблицы от базы. Сумма `tables[]` НЕ равна `databaseBytes` (системные схемы в списке отсутствуют).

Поля размеров без `omitempty`: `0` — значимое значение (пустая таблица занимает 0 байт).

**Breaking change (v0.12.0):** формат поля `tables` изменён с `[]string` на `[]{name, rowCount}`.

---

## GET /v1/admin/tables/{name}

Содержимое таблицы с пагинацией и сортировкой.


**Query Parameters:**

| Параметр | Тип | По умолчанию | Описание |
|----------|-----|--------------|----------|
| page | int | 1 | Номер страницы |
| size | int | 50 | Размер страницы (max 100) |
| sort | string | — | Поле сортировки (валидируется по реальным колонкам) |
| order | string | ASC | Направление: ASC / DESC |

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "table": "sessions",
  "columns": ["id", "model", "system_prompt", "temperature", "created_at", "updated_at"],
  "rows": [
    {"id": "a1b2c3d4-...", "model": "anthropic/claude-sonnet-4", "temperature": 0.7}
  ],
  "total": 150,
  "page": 1,
  "size": 50
}
```

Допускаются только таблицы из schema=public (whitelist через `pg_catalog.pg_tables`).

---

## POST /v1/admin/prompts

Создание нового промпта.


**Request:**
```json
{
  "productId": "uuid (optional, default → default-продукт)",
  "name": "code_review",
  "displayName": "Ревью кода (опционально, пустая строка ОК)",
  "description": "Ревью кода",
  "template": "Проведи ревью следующего кода..."
}
```

**Response 201:** `{"success": true}` (envelope).

**Ошибки:** `409 PROMPT_EXISTS` — промпт с таким `(productId, name)` уже существует. Один и тот же `name` может существовать в разных продуктах.

**Замечание про default_template (F-000010, v0.21.0):** при создании промпта `default_template` инициализируется значением `template` (initial = default). Последующие правки через `PUT` будут diff'иться с этим initial-value; `POST /reset` вернёт к нему. Если нужно «обновить дефолт» — это делается миграцией (см. CLAUDE.md «Миграции, правящие промпты» в репо сервера), не через API.

---

## DELETE /v1/admin/prompts/{name}

Удаление промпта. Системные промпты (`_system_*`) защищены от удаления. Status: `implemented`.

**Query parameters:** `productId` — UUID продукта, опциональный (default-продукт без параметра).

**Response 200:** `{"success": true}` (envelope).

**Ошибки:**
- `404 PROMPT_NOT_FOUND` — промпт с таким `(productId, name)` не найден
- `403 SYSTEM_PROMPT_PROTECTED` — системные промпты нельзя удалять

---

## PATCH /v1/admin/prompts/{name}/toggle

Переключение `isActive` промпта. Status: `implemented`.

**Query parameters:** `productId` — UUID продукта, опциональный (default-продукт без параметра).

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "name": "code_review",
  "isActive": false
}
```

---

