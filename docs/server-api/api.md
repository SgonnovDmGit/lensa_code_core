# SwanCode Server API

Base URL: `http://localhost:8080`

Версия сервера определяется константой `internal/version/version.go` и доступна через `GET /v1/admin/server-info` (admin-only). На момент написания: **0.59.0** (последняя shipped — см. `Changelog.md`; v0.59.0 — reasoning: bugfix round-trip'а `reasoning_details` в живой петле (REQ-004 — v0.58.0 клал подпись только в персист, а петля читает in-memory) + показ размышлений `reasoningText` клиенту/админке (REQ-008) + управление уровнем `reasoningEffort` `none|minimal|low|medium|high` с эхом фактического (REQ-007); миграция 047; v0.58.0 — reasoning round-trip: `reasoning_details` (thought-signature reasoning-моделей) сохраняется на assistant-ходе и эхается обратно в Router, восстанавливая рвущиеся agentic-петли Gemini/GLM/Claude (REQ-004 / ANNOUNCE-002); внутреннее, клиентский контракт не меняется, миграция 046; v0.57.0 — console batch Lensa_Query: `isError` per-toolCall в истории (REQ-027) + `title` в 202 async (REQ-029) + `[]`-never-`null` в ChatResponse (REQ-030) + GET-поллинг вне attestation-гейта (REQ-026); v0.56.3 — grace-кеш валидации per-user ключа сглаживает моргания InfraConnect + недоступность IC при проверке ключа отвечает `503 KEY_VALIDATION_UNAVAILABLE` вместо `500`, F-000029; v0.56.2 — admin query-versions отдаёт `queryText` для всех версий (REQ-036) + test-download стал канал+download пробом с fallback на продукт с релизом (REQ-039); v0.56.1 — `list_objects` до всех 16 типов метаданных + пороги namePattern 50→20, REQ-025 Lensa_Query).

---

## Общие заголовки

### ServiceInfo

Все JSON-ответы содержат поле `serviceInfo` с признаком успеха:
```json
{"serviceInfo": {"success": true}}
```
При ошибке `success: false` и код ошибки в `errorCode`:
```json
{"serviceInfo": {"success": false, "errorCode": "ERROR_CODE"}, "message": "..."}
```

### Аутентификация (двухключевая)

Identity разделена по двум заголовкам:

- **`Authorization: Bearer <key>`** — identity **человека/программы-агента**:
  - `swan_ak_{prefix}_{secret}` — admin-ключ
  - `swan_uk_{prefix}_{secret}` — user-ключ (серверо-локальный, выпускается через OTP / admin)
  - `{prefix}_{live|test}_{39 base62}` — **InfraConnect per-user API-ключ** (F-000029, v0.47.0): персональный ключ, выпущенный платформой InfraConnect (напр. `lens_live_…`). Сервер валидирует его S2S-раундтрипом к InfraConnect (`POST /v1/auth/api-keys/validate`, кэш 5 мин) и резолвит в локального пользователя по `infraconnect_id`. Принимается везде, где принимается `swan_uk_*`. Это основной путь авторизации для CLI / консоли.
- **`X-Product-Key: <key>`** — identity **клиентского приложения**:
  - `swan_pk_{prefix}_{secret}` — product-ключ (зашит в сборку клиента)

`swan_pk_*` в `Authorization` **не принимается** — для product-идентификации используется только `X-Product-Key`. Это разделение нужно для chat-эндпоинтов, где требуется одновременно знать **кто** обращается (user) и **из какого приложения** (product) — для биллинга и аналитики соответственно.

Bootstrap admin-ключ выдаётся при первом старте сервера (см. лог `slog.Info "Bootstrap admin created"`).
Выпуск / ротация ключей: `POST /v1/admin/admins/me/keys` + `DELETE /v1/admin/admin-keys/{id}`.

### Attestation — пломба запроса (`X-QC-Token`, F-000017 / ротация F-000028, v0.43.0)

Дополнительный **необязательный** заголовок поверх двухключевой авторизации, доказывающий, что запрос собрала подлинная компонента QueryCrypt (а не самоделкин с украденными `swan_uk_`+`swan_pk_`). Применяется **только к маршрутам продукта `query_console_1c`** (`POST /v1/api/chat*`); прочие продукты и админка его не используют.

- **`X-QC-Token: <base64>`** — запечатанная пломба: AES-256-GCM-конверт QueryCrypt, внутри — компактный JSON, привязанный к пользователю (`SHA-256(swan_uk_)`), телу запроса (`SHA-256(body)`), времени (`ts`) и одноразовому `nonce`. Собрать её может только компонента с зашитым ключом.
- **Ключ ротируется (v0.43.0).** Сервер хранит историю версий ключа; новую рождает `POST /v1/admin/querycrypt/rotate` (one-time-reveal seed/mask для сборки dll). При проверке сервер trial-decrypt'ит токен по живым версиям:
  - **current** — полный доступ;
  - **retired в grace-окне** (`querycrypt.key_grace_days`, дефолт 30) — компонента устарела: в `enforce` → `426 COMPONENT_OUTDATED` (обновите dll; сам апдейт клиент тянет штатно — update-путь пломбой не гейтится);
  - вне grace / неизвестный ключ → `401 ATTESTATION_INVALID`.
- **Режим** управляется config-ключом `attestation.enforce` (`server_config`, hot-reload через `PUT /v1/admin/config`):
  - `observe` (дефолт) — отсутствие/невалидность/устаревание пломбы **логируется** и пишется в audit, запрос **пропускается** (расцепляет серверный релиз от выката новой dll);
  - `enforce` — пломба обязательна на `query_console_1c`.
- **Скоуп гейта — только мутирующие POST-хода чата**: `POST /chat`, `/chat/async`, `/chat/stream`, `/chat/tool-retry`, `/chat/tool-results`, `/chat/tool-results/async`. **`GET /chat/{ticketId}/result` (поллинг результата) пломбой НЕ гейтится** (REQ-026 Lensa_Query, `v0.57.0`): read-only, тела нет — `bodyHash`-пломба для него бессмысленна, и в `enforce` гейт лишь заставлял бы подписывать поллинг. Роуты вне chat-mux (`/v1/api/sessions*`, `/me`, `/models`, `/updates/*`) пломбу не проверяют вовсе.
- Исход проверки пишется в `user_auth_events` (event `attestation`, `metadata.outcome` = `valid|missing|invalid|outdated`, `metadata.mode`, `metadata.reason`).

Управление ключом — admin-эндпоинты `POST /v1/admin/querycrypt/rotate` (генерация+reveal) и `GET /v1/admin/querycrypt/keys` (история версий, seed/mask маскированы). Подробный flow и регламент ротации — `docs/flows/querycrypt-attestation.md`.

### Admin authentication (v0.24.2+)

`Authorization: Bearer <token>` поддерживает два формата:

- **Issued `swan_ak_<8hex>_<32hex>`** — стандартный admin key, выпускается через `POST /v1/admin/admins/me/keys` или `POST /v1/admin/admins/{id}/keys`. Plaintext возвращается **один раз** в response, после — bcrypt-only в БД.
- **Bootstrap-password (12..72 ASCII chars)** — задаётся оператором в env `ADMIN_API_KEY` ДО первого старта на пустой `admins`-таблице. Сервер делает `bcrypt(env)` и кладёт в `admin_keys` с sentinel-prefix `__bootstrap__`. После первого `POST /admins/me/keys` от этого admin'а — bootstrap-row авто-revoked, env-password перестаёт работать.

Полный flow и recovery scenarios: `docs/flows/admin-auth.md`.

Матрица доступа по эндпоинтам:

| Эндпоинт | `Authorization` | `X-Product-Key` |
|---|---|---|
| `GET /health` | — | — |
| `POST /v1/auth/otp/send`, `/v1/auth/otp/verify` | — | `swan_pk_*` (обязателен) |
| `GET /v1/api/me`, `/me/usage` | `swan_uk_*` (обязателен) | `swan_pk_*` (обязателен) |
| `GET/POST/PATCH/DELETE /v1/api/sessions*` (диалоги, batch-метаданные, история, версии запроса, удаление) | `swan_uk_*` (обязателен) | `swan_pk_*` (обязателен) |
| `POST /v1/feedback` | `swan_uk_*` (обязателен) | `swan_pk_*` (обязателен) |
| `GET /v1/api/updates/check`, `/updates/download` | `swan_uk_*` (обязателен) | `swan_pk_*` (обязателен) |
| `POST /v1/api/chat*` | `swan_uk_*` (обязателен) | `swan_pk_*` (обязателен) + `X-QC-Token` (пломба; обязателен только для `query_console_1c` при `attestation.enforce=enforce`) |
| `GET /v1/api/providers`, `/models`, `/models/{provider}`, `/balance/{provider}`, `/recommendations/{language}` | `swan_ak_*` (опц.) | `swan_pk_*` (опц.) — нужен **один** из двух |
| `/v1/admin/*` | `swan_ak_*` (обязателен) | — |

Где в матрице указан `swan_uk_*`, принимается также **InfraConnect per-user ключ** (`{prefix}_{live|test}_…`, F-000029) — он эквивалентен user-ключу после S2S-валидации.

Общие ошибки авторизации:
- `UNAUTHORIZED` (401) — отсутствует или неверный ключ в `Authorization` либо `X-Product-Key`
- `USER_API_KEY_INVALID` / `USER_API_KEY_EXPIRED` / `USER_API_KEY_PROJECT_MISMATCH` (401) — InfraConnect per-user ключ не прошёл валидацию (недействителен / истёк / выпущен в другом project). `USER_DEACTIVATED` (403) — аккаунт владельца ключа выключен. При временной недоступности InfraConnect (сеть / 5xx / нераспознанный код) — `KEY_VALIDATION_UNAVAILABLE` (503), не 401 и не 500: сервер проверки ключа недоступен, клиент может ретраить.
- `USER_REQUIRED` (403) — эндпоинт требует user-ключ, а в `Authorization` нет валидного `swan_uk_*`
- `PRODUCT_REQUIRED` (403) — эндпоинт требует product-ключ, а заголовок `X-Product-Key` отсутствует
- `ADMIN_REQUIRED` (403) — эндпоинт требует admin-ключ
- `AUTH_REQUIRED` (403) — dual-use эндпоинт (admin или product), нет ни одного валидного identity
- `INFRACONNECT_NOT_CONFIGURED` (503) — InfraConnect не настроен (пустой INFRACONNECT_BASE_URL)
- `UPDATES_NOT_CONFIGURED` (503) — SwanUpdates не настроен (пустой SWANUPDATES_BASE_URL или SWANUPDATES_SERVER_KEY)
- `AI_NOT_CONFIGURED` (503) — AI Router не настроен (пустые AIROUTER_BASE_URL/AIROUTER_CONSUMER_KEY); отвечают chat*, models, balance
- `ATTESTATION_REQUIRED` (403) — на `query_console_1c` при `attestation.enforce=enforce` отсутствует заголовок `X-QC-Token` (F-000017)
- `ATTESTATION_INVALID` (401) — пломба `X-QC-Token` не распечаталась / просрочена / реплей / не сошлась с пользователем или телом / ключ вне grace (F-000017)
- `COMPONENT_OUTDATED` (426) — пломба валидна, но собрана retired-ключом в grace-окне: компонента QueryCrypt устарела, требуется обновление (F-000028, только `enforce`)

### JSON-контракт

- **Все ключи** в запросах и ответах — `camelCase` (`sessionId`, `codeChanges`, `toolUseId`).
- **Пустой массив** — `[]`, никогда `null`.
- **Отсутствующее необязательное поле** — ключ не отправляется (`omitempty`), не `null`.
- **Потенциально опасное содержимое** (код, shell-команды) передаётся в `base64` в полях с суффиксом `B64`.

### Request ID

Каждый ответ содержит заголовок `X-Request-ID` — уникальный UUID запроса. Клиент может передать свой `X-Request-ID` в запросе — сервер вернёт его обратно.

### CORS

Сервер отдаёт заголовки CORS для всех ответов:
- `Access-Control-Allow-Origin: *`
- `Access-Control-Allow-Methods: GET, POST, PUT, PATCH, DELETE, OPTIONS`
- `Access-Control-Allow-Headers: Content-Type, Authorization, X-Request-ID, X-Product-Key, X-QC-Token, Range, If-Range`
- `Access-Control-Expose-Headers: X-Request-ID, X-Session-ID, X-Checksum-Sha256, Content-Disposition, ETag, Content-Range, Accept-Ranges`

Preflight (`OPTIONS`) возвращает `204 No Content`. `Range`/`If-Range` и expose-заголовки добавлены в v0.32.0 для download-эндпоинта обновлений (докачка + сверка checksum из браузерных клиентов).

### AI-маршрутизация: единственный провайдер — Swan_AiRouter (v0.33.0, cutover F-000007)

С v0.33.0 поставщик инференса — **только** микросервис Swan_AiRouter. Direct-интеграции 5 провайдеров (OpenRouter, Claude, ChatGPT, DeepSeek, GigaChat) удалены.

Для клиентов: канонический провайдер — **`SwanCode`** (`GET /v1/api/providers` возвращает только его). С **v0.40.0 (T-000148)** legacy-имена (`OpenRouter`, `Claude`, `ChatGPT`, `DeepSeek`, `GigaChat`) больше **не резолвятся как алиасы** — значение `provider` в запросах и path-параметрах принимается, но **игнорируется**: провайдер всегда `SwanCode`. До v0.40.0 они работали как алиасы; теперь это no-op (старые диалоги резюмируются и так, провайдер сессии больше не хранится).

Биллинг пользователей считается по **внутреннему прайсу** сервера (таблица `model_prices`, коины; админ-управление через `/v1/admin/model-prices`). Закупочные цены Router'а в клиентские ответы не попадают.

Конфигурация: env `AIROUTER_BASE_URL` + `AIROUTER_CONSUMER_KEY` (см. `.env.example`). Если не заданы — сервер живёт в деградации: chat/models/balance отвечают `503 AI_NOT_CONFIGURED`.

Ошибки Router'а мапятся в коды сервера: `MODEL_NOT_ALLOWED` (403), `AI_UNAVAILABLE` (503), `AI_TIMEOUT` (504), прочее → `AI_REQUEST_FAILED` (502); детали — только в errorlog.

---

# OTP-аутентификация (требует X-Product-Key)

Authn-события привязываются к product_id — каждый OTP-запрос должен идти с заголовком `X-Product-Key: swan_pk_*`. Authorization-заголовок отсутствует (юзера ещё нет).

## POST /v1/auth/otp/send

Отправить OTP-код на email или phone. Proxy в InfraConnect.

**Request:**
```json
{
  "email": "string, required если phone пусто",
  "phone": "string, required если email пусто"
}
```

Поля `email` и `phone` взаимоисключающие — нельзя передать оба.

**Нормализация identity (перед форвардом в InfraConnect):** `email` → lowercase + trim; `phone` → убираются пробелы/скобки/дефисы, остаётся `+` и цифры. Строгий E.164 (`+` с кодом страны) — ответственность клиента: InfraConnect требует `^\+\d{10,15}$` и отвергает phone без `+`.

**Response (200):**
```json
{
  "serviceInfo": {"success": true},
  "status": "string — статус от InfraConnect (product-level c v0.17.0: registration|login относительно ВАШЕГО продукта, не всей экосистемы)",
  "channel": "string — канал: email | sms",
  "retryAfter": 60,
  "codeLength": 6
}
```

**Errors:**
- `INVALID_JSON` (400) — невалидный JSON
- `INVALID_REQUEST` (400) — ни email, ни phone не указаны; или оба указаны
- `INFRACONNECT_NOT_CONFIGURED` (503) — INFRACONNECT_BASE_URL не задан
- `INFRACONNECT_ERROR` (502) — InfraConnect вернул ошибку

---

## POST /v1/auth/otp/verify

Проверить OTP-код и получить user_key. На success выпускается `swan_uk_*` ключ для дальнейших запросов.

**Request:**
```json
{
  "email": "string, required если phone пусто",
  "phone": "string, required если email пусто",
  "code": "string, required — OTP-код из email/sms"
}
```

`email`/`phone` нормализуются перед форвардом так же, как в `send` (lowercase email; strip-форматирование phone, E.164 — на клиенте).

**Response (200):**
```json
{
  "serviceInfo": {"success": true},
  "userId": "uuid — ID пользователя в нашей БД",
  "userKey": "swan_uk_{prefix}_{secret} — сохранить, передавать в Authorization: Bearer",
  "status": "string, opt — статус от InfraConnect (product-level c v0.17.0: registration|login относительно нашего продукта; UI-подсказка). Наш UPSERT по infraconnect_id идемпотентен и на status не завязан"
}
```

**Errors:**
- `INVALID_JSON` (400) — невалидный JSON
- `INVALID_REQUEST` (400) — code пуст; или email/phone не указаны / оба указаны
- `INFRACONNECT_NOT_CONFIGURED` (503) — INFRACONNECT_BASE_URL не задан
- `INFRACONNECT_ERROR` (502) — OTP неверен или истёк
- `USER_DELETED` (403) — аккаунт пользователя удалён
- `DB_ERROR` (500) — ошибка при UPSERT пользователя или выпуске ключа

**Side effects:**
- UPSERT в `users` по `infraconnect_id` (`ON CONFLICT DO NOTHING`)
- INSERT в `user_keys` (label=`otp`)

---

# Public API — User (требует `swan_uk_*` + `X-Product-Key: swan_pk_*`)

## GET /v1/api/me

Информация о текущем пользователе.

**Response (200):**
```json
{
  "serviceInfo": {"success": true},
  "id": "550e8400-...",
  "infraconnectId": "uuid, opt — присутствует для OTP-логин'нутых пользователей",
  "balanceCoins": 19850.5,
  "balanceReservedCoins": 0
}
```

- `balanceCoins` (с v0.41.0, F-000012) — read-proxy из Swan_Billing. Soft: при недоступном/отключённом Billing возвращается `0` (эндпоинт не падает).
- `balanceReservedCoins` — **всегда `0`**: резервы (холды) с v0.41.0 живут на стороне Swan_Billing (TTL-короткие), локально не трекаются. Поле сохранено для обратной совместимости.

**Errors:**
- `USER_NOT_FOUND` (404) — пользователь не найден или soft-deleted

---

## GET /v1/api/me/usage

История использования с cost breakdown. Пагинация: `page`, `size` (max 100).

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "usage": [
    {
      "model": "anthropic/claude-sonnet-4",
      "promptTokens": 1500,
      "completionTokens": 300,
      "totalTokens": 1800,
      "costCoins": 0.0495,
      "createdAt": "2026-04-10T12:00:00Z"
    }
  ],
  "total": 42,
  "page": 1,
  "size": 50
}
```

---

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
  - `toolCalls[].isError` (bool, REQ-027 Lensa_Query, `v0.57.0`) — исход выполнения тула: `true` = тул отработал с ошибкой (для мутирующих `apply_query_change`/`replace_table`/… чаще всего валидация запроса не прошла) → клиент рисует ✗; `false` = успех → ✓. Джойн по `toolUseId` с `tool_result` сессии (данные уже хранятся — surface existing, не новая модель; содержимое `tool_result` клиенту по-прежнему не отдаётся, только булев исход). **`omitempty`: присутствует ⟺ для вызова найден `tool_result`** — опускается для старых записей без результата и на живом ходе до прихода результата (клиент читает отсутствие мягко: рисует ✓/нейтрально). Живой поток исход знает из payload'а; это поле закрывает разрыв при перезагрузке диалога из истории.
- `messages[].model` / `temperature` / `realModel` (v0.50.0, F-000026) — attribution assistant-хода: requested model, requested temperature (explicit `0.0` is preserved), and actual Router/provider model if it differs from requested. Поля опускаются для legacy/user/tool rows без attribution; `realModel` опускается, если совпал с `model`.
- `messages[].promptTokens` / `completionTokens` / `totalTokens` / `costCoins` (T-000146) — расход AI-вызова, породившего это assistant-сообщение (`int` токены вход/исход/всего + стоимость в коинах). **Присутствуют ⟺ для сообщения есть usage-строка** (assistant-ход); у `user`/tool-сообщений опущены. Контракт присутствия как у `/chat`: отсутствие = «нет данных», **не** «ноль». `costCoins` — та же монета, что `costCoins`/`balanceCoins` в `/chat`. Удобно для отображения расхода прямо в ленте истории.
- `messages[].costUsd` / `costRub` / `modelDisplayName` (REQ-040 Lensa_Query, T-000242, `v0.60.1`) — паритет истории с live-ходом: `costUsd`/`costRub` — та же трата хода по **текущему** курсу продукта (`costCoins ÷ pricing.coins_per_usd|rub`; если курс менялся с момента хода — значения приблизительны, как у админ-`economics`; присутствуют вместе с `costCoins`); `modelDisplayName` — человекочитаемое имя фактической модели хода (`realModel`, fallback `model`) — клиент рисует чип модели без самостоятельной гуманизации технического id. `omitempty`.
- `messages[].genMs` / `tokensPerSec` (REQ-040, T-000242, `v0.60.1`) — окно и скорость генерации AI-вызова этого сообщения; семантика и контракт присутствия — см. `POST /v1/api/chat`. Исторические строки до v0.60.1 и промоутнутые async-ходы окна не имеют → поля опущены.
- Сообщения отсортированы по возрастанию `id` (хронологически). Для подгрузки более старых — `before` = `id` первого (верхнего) сообщения страницы.
- `id` — `SERIAL` (integer); в выдаваемом наборе порядок `id` совпадает с хронологией.

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
    "modelsUsed": ["Gemini 3.5 Flash", "Claude Sonnet 4.6"]
  }
}
```
- `session` — заголовок/модель диалога (как в `/messages`), `omitempty`.
- `summary.turnsCount` (int) — число ходов диалога = visible `user_text` сообщений («пар запрос→ответ»). **Tool-loop одного запроса = ОДИН ход** (AI-подвызовы петли не считаются). Всегда присутствует (`0` для пустого/нового диалога).
- `summary.promptTokens` / `completionTokens` / `totalTokens` (int) — Σ токенов по всем assistant-ходам. Присутствуют ⟺ есть хотя бы один измеренный ход (usage-строка).
- `summary.costCoins` (число) — Σ списаний в монетах; `costUsd`/`costRub` — по **текущему** курсу продукта (та же конвертация, что per-message REQ-040; приблизительно при смене курса). Присутствуют ⟺ Σ стоимости > 0 (billing-skipped ходы не создают стоимость); `usd`/`rub` — при `costCoins` и соответствующем `rate>0`.
- `summary.genMs` (int) — Σ окон генерации; `tokensPerSec` (float) — **средняя** скорость диалога, посчитана сервером как `Σcompletion ÷ (ΣgenMs/1000)`. Присутствуют ⟺ `ΣgenMs>0` (и `Σcompletion>0` для `tokensPerSec`).
- `summary.modelsUsed` (array, опц.) — дружелюбные имена всех моделей диалога, дедуп, в порядке появления. Осмысленно после снятия model-lock (T-000247, мульти-модельные диалоги). Пустой список опущен.
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

# Public API — Chat (требует `swan_uk_*` + `X-Product-Key: swan_pk_*`)

## POST /v1/api/chat

v0.50.0 chat correctness notes:
- `temperature` is tri-state: omitted or `null` inherits session/default; explicit `0.0` is valid; values outside `[0.0, 2.0]` return `400 INVALID_TEMPERATURE`.
- On existing sessions, requested `model`/`temperature` are per-turn until the AI response succeeds. `sessions.model/temperature` are last-success state, not last-attempt state.
- Assistant messages store per-message attribution (`model`, `temperature`, optional `realModel`).

Отправка сообщения AI-провайдеру. Единый вход для всех провайдеров.

**Заголовки:** обязательны оба — `Authorization: Bearer swan_uk_*` (identity пользователя для биллинга) и `X-Product-Key: swan_pk_*` (identity приложения для аналитики и tool-set'а).

**Биллинг (с v0.41.0 — внешний Swan_Billing, F-000012):** перед вызовом провайдера сервер резервирует worst-case стоимость (Hold) в Swan_Billing. После ответа списывается реальная стоимость (settle через durable-outbox), остаток возвращается. При недостатке средств — `402 INSUFFICIENT_BALANCE`; при недоступном/несконфигуренном Billing — `503 BILLING_NOT_CONFIGURED` (hard-dependency, AI не вызывается). Стоимость в коинах считает сервер (`model_prices` + курс); Billing — чистый реестр коинов.

**Ограничение моделей (F-000015):** если у продукта настроен allow-list моделей и запрошенная `model` в него не входит — `403 MODEL_NOT_ALLOWED` (проверка ДО резерва баланса и вызова провайдера). Сбой проверки allow-list — `500 MODEL_ACCESS_CHECK_FAILED` (fail-closed, не pass-through). Применяется ко всему семейству `/v1/api/chat*` (`/stream`, `/tool-results`, `/tool-retry`). Пустой allow-list продукта = все модели разрешены. Управление списком — `GET`/`PUT /v1/admin/products/{id}/models`.

**Request:**
```json
{
  "sessionId": "",
  "provider": "SwanCode",
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
| provider | string | **нет** | **Опционально с v0.47.0** — Router-only, провайдер один: значение игнорируется (всегда канон `SwanCode`), поле можно не присылать. Принимается для обратной совместимости. (Ранее пустая строка → `400 PROVIDER_REQUIRED`; этот код снят.) |
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
  "balanceCoins": 19850.5,
  "costCoins": 12.5,
  "costUsd": 0.0625,
  "costRub": 6.25,
  "userMessageId": 4242,
  "reasoningText": "Нужно посчитать обороты по счёту 60...",
  "reasoningEffort": "high",
  "genMs": 8420,
  "tokensPerSec": 59.4
}
```

**Поля `reasoningText` / `reasoningEffort` (REQ-008 / REQ-007, опциональные, v0.59.0):** размышления модели этого хода. `reasoningText` (string) — человекочитаемый текст размышлений (нормализован Router'ом; сырой опаковый `reasoning_details` клиенту не отдаётся). `reasoningEffort` (string, `none\|minimal\|low\|medium\|high`) — **фактически применённый** уровень (эхо Router после мягкого клэмпа: на не-reasoning модели вернётся `none`, даже если запрошен `high`). Оба `omitempty` — присутствуют ⟺ в ходе реально были размышления. Те же поля — per-assistant-ход в `GET /v1/api/sessions/{id}/messages` (реплеятся при перезагрузке диалога) и в `result` при `completed`. **Caveat (сторона Router):** Gemini через провайдера ProxyAPI даёт upstream-400 на `reasoningEffort`>none (их шлюз без `thinkingConfig`); через BotHub — штатно.

**Поле `title` (REQ-021 Lensa_Query, опциональное):** заголовок диалога. На **создании** сессии (первый обмен без `sessionId`) — непустой, выведён из первого сообщения пользователя (схлопывание пробелов + обрезка до 60 символов); убирает «прыжок» имени на клиенте, который иначе ставит временное «Диалог N» и подтягивает серверное имя отдельным запросом метаданных. На **продолжении** существующей сессии — текущий заголовок (актуален и после переименования). `omitempty`: опускается, если заголовок не посчитан (вырожденный ввод). Клиент читает мягко — отсутствие поля не ломает. То же поле — в `result` ответа `GET /v1/api/chat/{ticketId}/result` при `completed`.

**Поле `userMessageId` (T-000107, опциональное):** SERIAL id персистнутого сообщения пользователя этого хода — стабильный якорь user-хода (равен `id` соответствующего сообщения в `GET /v1/api/sessions/{id}/messages`). Позволяет клиенту действовать на конкретный user-ход (attribution версий, retry) без отдельного запроса истории. Опускается (`omitempty`), если сообщение не удалось сохранить (id=0).

**Поля `balanceCoins` / `costCoins` (T-000106, опциональные):**
- `balanceCoins` (float) — остаток монет пользователя **после** списания за этот ход. Равен `balanceCoins` из `GET /v1/api/me` (gross — НЕ вычитает активные резервы параллельных ходов). Клиент обновляет баланс в шапке без отдельного запроса к `/me`.
- `costCoins` (float) — стоимость этого хода в монетах (списание с наценкой).
- **Контракт присутствия:** оба поля присутствуют **⟺ реальное списание (commit) прошло**. Отсутствуют (`omitempty`) при отключённом биллинге, непосчитанной цене модели или фейле commit'а. **Отсутствие читать как «неизвестно», НЕ как «бесплатно»/«ноль»** — при нулевом балансе поле присутствует со значением `0`.

**Поля `costUsd` / `costRub` (T-000210, опциональные):** стоимость этого хода в USD и рублях по **настоящему** курсу — `costUsd = costCoins ÷ pricing.coins_per_usd`, `costRub = costCoins ÷ pricing.coins_per_rub`. Это **трата клиента** (что он заплатил по курсу; наценка уже зашита в `costCoins`). Тот же контракт присутствия, что у `costCoins` (есть ⟺ commit прошёл). **Закупочную цену Router'а (COGS) сервер клиенту НЕ отдаёт** — она доступна только в админ-эндпоинтах (`economics`, см. `GET /v1/admin/sessions`). Те же поля — в `result` ответа `GET /v1/api/chat/{ticketId}/result` и в ответах `/chat/tool-results` / `/chat/tool-retry`.

**Поля `genMs` / `tokensPerSec` (REQ-040 Lensa_Query, T-000242, опциональные, v0.60.1):** время и скорость генерации финального AI-вызова хода. `genMs` (int) — окно генерации в миллисекундах, замеряется сервером как elapsed sync-вызова Router. `tokensPerSec` (float) — скорость, считается **сервером**: `completionTokens ÷ (genMs/1000)`. **Контракт присутствия:** `omitempty`, присутствуют ⟺ окно измерено; отсутствие = «нет данных», не ноль. Не измеряется (поля опущены): промоутнутый async-тикет (Router отдал `202`, ответ пришёл поллингом — окно неизвестно); `tokensPerSec` дополнительно опущен при нуле completion-токенов. Те же поля — в `result` при `completed`, в ответах `/chat/tool-results` / `/chat/tool-retry` и per-assistant-ход в `GET /v1/api/sessions/{id}/messages`.

| Поле | Тип | Описание |
|------|-----|----------|
| model | string | Технический ID модели (равен полю `model` в запросе) — используется биллингом и системой recommendations |
| modelDisplayName | string | Человекочитаемое имя для UI («Claude Opus 4.6», «GPT-5»). Резолв (T-000250, override-aware): **admin-оверрайд** (`model_display_overrides`) по фактической, затем запрошенной модели → каталог Router → `HumanizeName(id)`. Оверрайд — та же авторитетная админ-метка, что в списке моделей (`GET /v1/api/models`) и админ-прайсе; чат/`/chat/tool-results[/async]`/история совпадают с ними. **Паритет форм (T-000248):** датированный real-снапшот (`…-20260519`, нет в каталоге) ловится по запрошенной модели (каталожный ключ, одинакова во всех ответах хода). Клиент может выводить `modelDisplayName ?? model`. Поле `omitempty` |
| codeChanges | array | Структурированные изменения кода из ответа (если есть). Клиент должен декодировать `searchB64`/`replaceB64` из base64 перед применением. Заполняется только если AI вызвал `apply_code_change` (только для default-продукта); для не-default продуктов — всегда `[]` |
| executeCommands | array | Команды для выполнения из ответа (если есть). Заполняется только если AI вызвал `execute_command` (только для default-продукта); для не-default продуктов — всегда `[]` |
| toolUses | array | Generic-канал tool_use'ов (все non-legacy тулы, см. `Tool surface` ниже). Для default-продукта v0.23.0+ — заполнен для `oneC_*` UIA-тулов; для query_console_1c — для всех 20 console-тулов; legacy `apply_code_change`/`execute_command` сюда НЕ попадают (они в `codeChanges`/`executeCommands`). Сериализуется как массив (не `omitempty`) |

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

Ошибки preflight (`INVALID_JSON`, `MODEL_REQUIRED`, `INVALID_TEMPERATURE`, `INVALID_ASYNC_WAIT`, `MODEL_NOT_ALLOWED`, billing errors) возвращаются синхронно, как у `POST /v1/api/chat`.

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

---

## POST /v1/api/chat/stream

**Status: deprecated — с v0.33.0 endpoint возвращает безусловный `501 STREAMING_NOT_SUPPORTED`** (до парсинга тела, без создания сессии и резерва баланса). SSE-путь через AI Router не реализован; вернётся отдельным релизом (T-000141 — Router-SSE контракт готов на стороне Router'а). Для всех сценариев использовать `POST /v1/api/chat`.

**Response 501:**
```json
{
  "serviceInfo": {"success": false, "errorCode": "STREAMING_NOT_SUPPORTED"},
  "message": "Стриминг временно не поддерживается — используйте POST /v1/api/chat"
}
```

> Исторический SSE-контракт (поток + `event: done` с balanceCoins/costCoins/userMessageId) действовал до v0.32.x и вернётся с реализацией Router-SSE (planned).

---

## POST /v1/api/chat/tool-retry

Повторная попытка AI после неудачного применения инструментов на стороне клиента. Клиент вызывает этот эндпоинт, если `apply_code_change` или `execute_command` не удалось выполнить: SEARCH не найден в файле, билд упал после правки, команда завершилась с ошибкой.

Сервер добавляет скрытое `tool_result` сообщение в сессию, повторно вызывает AI с полным контекстом и возвращает новый ответ.

**Request:**
```json
{
  "sessionId": "a1b2c3d4-...",
  "failures": [
    {
      "toolUseId": "tu_b3f4a12e5c6d",
      "failureType": "search_not_found",
      "message": "Фрагмент search не найден в файле main.go",
      "contextB64": "cGFja2FnZSBtYWluCgpmdW5jIG1haW4oKSB7fQo="
    }
  ]
}
```

| Поле | Тип | Обязательно | Описание |
|------|-----|-------------|----------|
| sessionId | string | да | UUID сессии из предыдущего ответа |
| failures | array | да | Минимум один элемент |

**Поля `failures[]`:**

| Поле | Тип | Обязательно | Описание |
|------|-----|-------------|----------|
| toolUseId | string | да | `toolUseId` неудавшегося вызова из предыдущего ответа |
| failureType | string | да | `search_not_found` \| `file_not_found` \| `build_failed` \| `command_failed` \| `internal` (v0.61.1, T-000246 — внутренняя ошибка клиента: тул не удалось ВЫЗВАТЬ; НЕ AI-fixable) |
| message | string | да | Краткое описание проблемы |
| contextB64 | string | нет | base64 с произвольным контекстом: содержимое файла, вывод линтера, stderr команды |

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "sessionId": "a1b2c3d4-...",
  "content": "Попробую по-другому...",
  "model": "anthropic/claude-sonnet-4",
  "modelDisplayName": "Claude Sonnet 4",
  "codeChanges": [...],
  "executeCommands": [...],
  "toolUses": [],
  "attemptsUsed": 1,
  "attemptsMax": 2,
  "balanceCoins": 19850.5,
  "costCoins": 12.5
}
```

Поле `modelDisplayName` — человекочитаемое имя модели для UI (см. описание в `POST /v1/api/chat`); может отсутствовать (`omitempty`).

Поля `balanceCoins` / `costCoins` — опциональны, тот же контракт присутствия, что в `POST /v1/api/chat` (присутствуют ⟺ commit прошёл). В ветке «лимит retry исчерпан» (ниже) списания нет → поля отсутствуют.

Поле `toolUses` — generic-канал tool_use'ов (см. `Tool surface` секции ниже). Для default-продукта v0.23.0+ содержит `oneC_*` тулы; для query_console_1c — все console-тулы.

**Если лимит retry исчерпан** (`attemptsUsed >= attemptsMax`):
```json
{
  "serviceInfo": {"success": true},
  "sessionId": "a1b2c3d4-...",
  "content": "Лимит автоматических повторных попыток исчерпан.",
  "retryExhausted": true,
  "attemptsUsed": 2,
  "attemptsMax": 2
}
```

Сервер больше не обращается к AI — клиент должен показать пользователю накопленные ошибки.

**Лимит `attemptsMax`** настраивается runtime через `PUT /v1/admin/config` по ключу `ai_tool_retry.max_attempts` (дефолт `5` с `v0.59.1`). **Считаются только ПОДРЯД идущие фейлы** (`v0.59.1`): `attemptsUsed` = число консекутивных tool-раундов с ошибкой в хвосте истории. Успешный tool-раунд (модель добилась прогресса) и новое пользовательское сообщение **сбрасывают счётчик** — успешные агентские раунды и server-inline результаты лимит не жгут. До `v0.59.1` считались все tool_result без разбора, из-за чего длинная агентская сессия упиралась в лимит на первом же реальном фейле.

---

## POST /v1/api/chat/tool-results

Полноценный endpoint для результатов выполнения инструментов — принимает и success, и failure.

Опц. поле `queryText` (string) — как в `POST /v1/api/chat`: текущий нормализованный текст запроса для пассивного версионирования (F-000014). На tool-results изменение текста атрибутируется `author=user`; при наличии в `results[]` apply-результата с `applied` пишется server-версия (агентская правка) + резолв accepted. **Present-vs-absent (T-000244, v0.61.1):** ключ отсутствует → no-op; присутствует, включая `""` → авторитетная актуализация (client-версия «пустой запрос» тоже пишется). Опц. `queryValidation` — как в `POST /v1/api/chat` (T-000245).

**Request:**
```json
{
  "sessionId": "a1b2c3d4-...",
  "results": [
    {
      "toolUseId": "tu_abc123",
      "status": "success",
      "message": "Applied to main.go"
    },
    {
      "toolUseId": "tu_def456",
      "status": "failure",
      "failureType": "command_failed",
      "message": "Command exited with code 1",
      "contextB64": "base64(stderr)"
    }
  ]
}
```

**Поля `results[]`:**

| Поле | Тип | Описание |
|------|-----|----------|
| toolUseId | string | ID вызова инструмента |
| status | string | `"success"` или `"failure"` |
| failureType | string | Тип ошибки: legacy — `search_not_found`, `file_not_found`, `build_failed`, `command_failed`; **`internal` (v0.61.1, T-000246, оба пути legacy/generic)** — внутренняя ошибка клиента (тул не удалось вызвать: исключение рантайма, обрыв). НЕ AI-fixable: сервер персистит пару tool_use↔result с маркером `INTERNAL_CLIENT_ERROR`, НЕ пере-гоняет ход через AI и отвечает terminal (`internalError: true` в ответе, см. ниже); retry-cap не тратится (следующее сообщение пользователя сбрасывает счётчик) |
| message | string | Описание результата (legacy) |
| contextB64 | string | Base64 контекст для legacy failure (stderr, содержимое файла) |
| name | string | (generic) Имя тула. Если присутствует — сервер выбирает product-specific unpacker для разворачивания `dataB64`. Если отсутствует — legacy путь (`failureType`/`message`/`contextB64`). **v0.23.0:** обязателен для `oneC_*` тулов default-продукта; рекомендуется для всех тулов (см. ANNOUNCE-003). |
| dataB64 | string | (generic) Base64-JSON payload результата. Формат payload'а зависит от тула — см. секции `Tool surface for query_console_1c` / `Tool surface for default (oneC)` ниже |

**Dispatch (server-side):** наличие непустого `name` переключает обработку результата на generic-путь. Иначе используется legacy-логика swan_code_client. Mixed-product сессии исключены by design — `name` валидируется против tool-set'а продукта сессии; AI, отдавший tool неизвестный для product, получит `UNKNOWN_TOOL` (HTTP 400).

**Валидация `results[]` против последнего assistant_tool_use** (T-000056): сервер сверяет присланные `toolUseId` с tool_use'ами из последнего скрытого `assistant_tool_use` сообщения сессии. Любое нарушение → 400 без обращения к провайдеру.

**Internal-фейл → terminal (v0.61.1, T-000246):** если среди `results[]` есть `failureType: "internal"`, сервер персистит все результаты (пары закрыты), записывает версии из `queryText` (если прислан), но AI **не вызывает** — ответ `200` в форме retry-ответа с `"internalError": true` и человекочитаемым `content` («ход остановлен, повторите действие»). Клиент показывает пользователю ошибку выполнения, НЕ ответ AI. То же для `/chat/tool-results/async` (terminal 200 вместо 202 — консольная обработка 200-на-продолжении, T-000003).

**Errors:**
- `NO_PENDING_TOOL_USES` (400) — в сессии нет ожидающих результата tool_use (последнее ассистент-сообщение не содержит tool_use). Возможные причины: AI завершил диалог текстом, клиент уже подтвердил все tool_use, неправильный sessionId.
- `UNKNOWN_TOOL_USE_ID` (400) — присланный `toolUseId` не найден в последнем assistant_tool_use. Сообщение содержит offending id.
- `DUPLICATE_TOOL_RESULT` (400) — один `toolUseId` встречается в `results[]` более одного раза. Сообщение содержит дублирующийся id.
- `MISSING_TOOL_RESULT` (400) — последний assistant_tool_use содержит tool_use, для которых нет результата в `results[]`. Сообщение перечисляет недостающие id.
- `UNKNOWN_TOOL` (400) — для generic-пути: `name` не входит в tool-set продукта сессии.
- `INVALID_TOOL_DATA` (400) — для generic-пути: `dataB64` невозможно распаковать в payload, ожидаемый product-specific unpacker'ом.
- `SESSION_NOT_FOUND` (404) — `sessionId` не существует в in-memory store.
- `INSUFFICIENT_BALANCE` (402) — не хватает баланса под worst-case стоимость очередного AI-хопа.
- `BILLING_NOT_CONFIGURED` (503) — Swan_Billing не сконфигурён или недоступен (hard-dependency, F-000012). AI не вызывается, резерв не делается.
- `MODEL_NOT_ALLOWED` (403) — модель сессии не входит в allow-list продукта (F-000015). Проверка ДО любых side-effect'ов.
- `MODEL_ACCESS_CHECK_FAILED` (500) — сбой загрузки allow-list (fail-closed).
- `AI_REQUEST_FAILED` (502) — провайдер вернул ошибку.

**Response 200:** тот же формат что у `tool-retry`.

---

## POST /v1/api/chat/tool-results/async

Неблокирующий async-вариант `POST /v1/api/chat/tool-results` (REQ-023 Lensa_Query, v0.55.0) — продолжение многораундовой цепочки без заморозки клиента. Симметричен паре `POST /v1/api/chat/async` + `GET /v1/api/chat/{ticketId}/result`, но для отправки результатов тулов, а не первого сообщения.

**Request:** то же тело, что у синхронного `/chat/tool-results` (`sessionId`, `results[]`, опц. `queryText`), плюс опциональное `asyncWaitMs`.

| Поле | Тип | Обязательно | Описание |
|------|-----|-------------|----------|
| sessionId | string | да | UUID существующей сессии (продолжение хода — автосоздание не поддерживается) |
| results | array | да | Результаты тулов — формат идентичен `/chat/tool-results` |
| queryText | string | нет | C3-актуализация текста запроса на этом раунде (как у `/chat/tool-results`, вкл. present-vs-absent семантику T-000244) |
| queryValidation | object | нет | Валидность queryText (T-000245, как у `/chat`) |
| asyncWaitMs | int | нет | Принимается для симметрии с `/chat/async` и валидируется на диапазон `[100, 30000]` (`400 INVALID_ASYNC_WAIT` вне диапазона), но **на поведение не влияет** — см. ниже |

**Поведение — всегда `202`.** В отличие от `/chat/async` (где быстрый ответ за `asyncWaitMs` отдаётся синхронно `200`), на продолжении цепочки сервер **всегда** возвращает `202` + `ticketId` (fast-path `200` не держится: на раунде результатов тулов модель ещё «думает» над ними, окно почти никогда не успевает). Клиент **всегда** продолжает через поллинг.

**Response 202:**
```json
{
  "serviceInfo": {"success": true},
  "ticketId": "2df9c7fc-19e8-4c99-9a83-bb2185fcff31",
  "status": "pending",
  "sessionId": "a1b2c3d4-..."
}
```

**Поллинг** — тот же `GET /v1/api/chat/{ticketId}/result` (нового эндпоинта поллинга нет). При `completed` — `result` = обычный `ChatResponse` следующего хода (с `toolUses` или финальный без них). Серверный tool-loop (server-only re-submit / mixed, C3 §3G) доводится в фоне, как у `/chat/async`.

**Errors:** preflight-ошибки (валидация `results[]` против незакрытых tool_use, `NO_PENDING_TOOL_USES`, `UNKNOWN_TOOL`, `INVALID_TOOL_DATA`, `SESSION_NOT_FOUND`, billing, `MODEL_NOT_ALLOWED`, `INVALID_ASYNC_WAIT`) возвращаются **синхронно**, как у `/chat/tool-results`. Исключение: исчерпание retry-лимита (только failure-результаты сверх `ai_tool_retry.max_attempts`) отдаётся синхронным `200` с `retryExhausted:true` — AI-продолжения нет, тикет не создаётся.

---

## Tool surface for lensa_universal (swan_code_universal)

Продукт `lensa_universal` (LenSa Universal — публичный универсальный кодинг-клиент, REQ-018, v0.60.1) — **«чистый чат без тулов»**: tool-set продукта пуст, `tools` в запрос к AI не отправляются, инструкции tool_use в системный промпт не подмешиваются. В ответах `toolUses` / `codeChanges` / `executeCommands` всегда `[]`. Любой присланный `tool_result` отклоняется `400 UNKNOWN_TOOL`. Полноценный tool-surface для Universal — отдельным REQ клиента (v0.14.0+ на его стороне).

## Tool surface for default (swan_code_client)

Default-продукт (1С-кодинг-клиент) использует **гибридный** tool-surface: **2 legacy-тула** (`apply_code_change`, `execute_command`) уходят клиенту в типизированных DTO-массивах `codeChanges` / `executeCommands` (см. выше); **6 onec_* тулов** (REQ-017, v0.23.0) — через generic-канал `toolUses[]` с `name + dataB64` payload.

### Legacy (apply_code_change, execute_command)

См. описание `codeChanges` / `executeCommands` в `POST /v1/api/chat` выше. Возврат от клиента — через legacy-поля в `ToolResultItem` (`failureType`, `message`, `contextB64`), `name` опционален.

### onec_* (v0.23.0 — mock-стадия)

UIA-операции в 1С-конфигураторе. На v0.23.0 — серверная сторона декларирует имена и schemas, unpacker'ы возвращают `PLACEHOLDER: payload-shape ... not yet finalized` — payload-формы будут согласованы через follow-up к REQ-017 (`clarification-needed` receipt).

**Tool — `open_oneC_object(path)`**: открыть объект конфигурации по пути (`Справочник.Контрагенты`, `Документы.X.Форма.Y`).

**Tool — `read_oneC_configuration_tree()`**: структурированное дерево конфигурации (без параметров).

**Tool — `read_oneC_messages_panel()`**: содержимое нижней панели «Сообщения» (без параметров).

**Tool — `run_oneC_module_check(moduleId?)`**: «Проверка модуля» в конфигураторе. После — клиент читает результат из панели сообщений.

**Tool — `oneC_debug_action(action, line?)`**: debug-действия. `action` — string (не enum); ожидаемые значения `save_and_close`, `start_debug`, `toggle_breakpoint` (требует `line`), `step_over`, `step_into`, `continue`. Клиент возвращает failure для неизвестных action'ов.

**Tool — `add_oneC_attribute(objectPath, attributeName, attributeType)`**: добавить реквизит в объект через UIA-диалог.

**Возврат от клиента (для всех 6 тулов):** `name = "<tool-name>"` + `dataB64 = "<base64-JSON>"`. Точные shape'ы payload'ов TBD. Сервер на v0.23.0 разворачивает в PLACEHOLDER-текст для AI; когда клиентская команда финализирует payload — серверный unpacker обновляется в отдельном релизе без breaking changes API.

## Tool surface for query_console_1c

Набор тулов, активный для сессий product=`query_console_1c`. Сервер заявляет AI **22 тула** через native tool_use (F-000002 + v0.22.0 extension + v0.25.0 buffer-семантика + v0.26.0 — 4 новых read-only тула; v0.44.0 — `find_account`; v0.48.0 — `get_account_subkonto`; v0.53.0 — `get_journal_documents`; v0.54.0 — server-executed `get_latest_query_version`; v0.61.0 — server-executed `load_skill`, F-000033). Результаты client-executed тулов возвращает клиент 1С через generic-форму `POST /v1/api/chat/tool-results` с полями `name` + `dataB64`; server-executed тул (Группа 6) исполняет сам сервер, в DTO ответа он не попадает.

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
- **Server-tool loop.** Ответ AI с server-tool сервер исполняет инлайн и **пере-вызывает AI** (sync — в том же запросе; async — пере-сабмит тикета в pending, клиент продолжает поллить), пока ответы содержат только server-tools. Mixed-ответ (server + client tools) → server исполнен инлайн, client-tools бунсятся клиенту как обычно. Кап итераций — `console1c.server_tool_max_iters` (дефолт 3). Каждая итерация — отдельный reserve/commit биллинга по залоченному на ход курсу. Клиентский контракт (`/chat/tool-results` с client-tools) **не меняется** — server-tools для 1С прозрачны.

**Tool 22 — `load_skill`** (`{name}`, F-000033, v0.61.0):
- **Server-executed** (как tool 13, клиент его не видит). Подгружает доменный knowledge-пак (skill) по требованию модели: каталог активных скиллов продукта (`{name, description}`) инжектится в системный промпт, тело сервер возвращает в `tool_result` из таблицы `skills` — оно оседает в истории диалога, повторная загрузка гасится dedup'ом tool_result. Стартовые паки консоли: `buh_skill` / `zup_skill` / `trade_skill`.
- Неизвестное/неактивное имя → inline error-`tool_result` `SKILL_NOT_FOUND: '<имя>'. Доступные: ...` — модель самокорректируется, ход не рвётся.
- Управление паками — `/v1/admin/skills` (см. Admin: Skills).

### Формат `n` / `s` (metadata)

В raw-payload 1С присылает имя в `n` и опционально синоним в `s`. Синоним добавляется в LLM-проекцию строкой `- Имя — Синоним` только если он отличается от auto-формы (capitalize + space-split по PascalCase). Это снижает дублирование без потери информации (см. spec F-000002 §8).

---

# Public API — Получение данных (bearer `swan_pk_*` или `swan_ak_*`)

## GET /v1/api/providers

Список доступных провайдеров. С v0.33.0 — только канонический `SwanCode`
(legacy-имена остаются принимаемыми алиасами в запросах, но в списке не светятся).

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "providers": ["SwanCode"]
}
```

---

## GET /v1/api/models  (и legacy `GET /v1/api/models/{provider}`)

С v0.47.0 провайдер в пути **опционален** (Router-only — провайдер один). Клиент может звать `GET /v1/api/models` без провайдера; форма `/{provider}` сохранена для совместимости, значение игнорируется (всегда канон `SwanCode`). Ниже — общий контракт ответа.



Список моделей. `{provider}` в пути принимается, но **значение игнорируется с v0.40.0**
(T-000148): каталог единый, источник — Router, ответ всегда под канон `SwanCode`. ID
моделей — канонический формат `vendor/name`.

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "provider": "SwanCode",
  "models": [
    {
      "id": "anthropic/claude-sonnet-4.6",
      "name": "Claude Sonnet 4.6",
      "subProvider": "anthropic",
      "contextLength": 1000000,
      "promptPriceCoins": 0.003,
      "completionPriceCoins": 0.015,
      "promptPriceCoinsPer1M": 3000,
      "completionPriceCoinsPer1M": 15000,
      "supportsTemperature": false
    }
  ]
}
```

**Поле `supportsTemperature` (T-000143, опциональное, v0.60.2):** `false` — модель задепрекейтила параметр `temperature` (Anthropic claude-5-семейство и т.п.): Router его такой модели не отправляет (self-heal с Router v0.45.0), настройка не имеет эффекта — клиенту рекомендуется прятать/дизейблить ползунок температуры для таких моделей. `true` — модель принимает `temperature`. `omitempty`: поле отсутствует, если Router его не сообщил (старая версия Router) — считать «неизвестно», UI не менять. Слать `temperature` в `/chat` безопасно в любом случае — на депрекейт-модели Router его молча дропнет.

**Поля цен (v0.33.0 — breaking; per-1M добавлены в v0.40.0):**
- `promptPriceCoins` / `completionPriceCoins` — coins per token, **внутренний прайс сервера** (источник биллинга, админ-управление через `/v1/admin/model-prices`). Модель без выставленной цены — нули (ход не тарифицируется).
- `promptPriceCoinsPer1M` / `completionPriceCoinsPer1M` (v0.40.0) — те же цены за 1M токенов (`×1e6`), для читаемости. На этом (auth'd) эндпоинте отражают **ручной** прайс (`model_prices`); авто-цены дневного каталога Router×курс заполняются на публичном `GET /v1/models/catalog`.
- USD-поля `promptPrice`/`completionPrice` и `priceOverridden` **удалены**: коин-цены с USD не коррелируют по определению (внутренняя валюта); закупка в USD доступна только админу через `GET /v1/admin/model-prices`.

**Display-name override (v0.49.0):** `name` по умолчанию приходит из Router-каталога. Если админ задал override через `PUT /v1/admin/model-display-overrides/{modelId...}`, сервер подменяет только `name` в `GET /v1/api/models`; `id` остаётся техническим canonical `vendor/name` и используется для allow-list, recommendations и биллинга.

**Фильтрация:**
- Состав и порядок каталога определяет Router (выдача consumer_access). Серверное курирование «последние 2 версии на семейство» удалено в v0.33.0.
- **Per-product allow-list (F-000015):** если запрос несёт `X-Product-Key` (и **не** admin-identity) и у продукта настроен allow-list — возвращаются только разрешённые модели, **в admin-заданном порядке** (`position`) (ключ пересечения — `id`). Admin-identity (`swan_ak_*`) → полный каталог без фильтра (для админ-UI чекбоксов, даже если запрос несёт и `X-Product-Key`). Пустой allow-list = все модели. Сбой загрузки списка → `500 MODEL_ACCESS_CHECK_FAILED` (fail-closed). Управление списком — `GET`/`PUT /v1/admin/products/{id}/models`.

---

## GET /v1/models/catalog

**Публичный** каталог моделей с коин-ценами (v0.40.0, F-000022). **Без аутентификации** —
для sales-лендинга до регистрации (S1). Rate-limit per-IP (120 запросов / 5 мин).
Невалидный/отсутствующий ключ игнорируется — эндпоинт всегда truly public.

Путь намеренно вне `/v1/api/*` (auth'd-namespace): это публичная поверхность.

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "provider": "SwanCode",
  "models": [
    {
      "id": "openai/gpt-4o-mini",
      "name": "GPT-4o Mini",
      "subProvider": "openai",
      "contextLength": 128000,
      "promptPriceCoins": 0.00003,
      "completionPriceCoins": 0.00012,
      "promptPriceCoinsPer1M": 30,
      "completionPriceCoinsPer1M": 120
    }
  ]
}
```

**Цены (только коины):**
- Отдаются **исключительно коины** (per-token + per-1M). USD/закупка наружу **не выводятся**.
- Фолбэк-цепочка коин-цены (§3): ручной коин-override (`model_prices`) → дневной каталог **Router-sell** (что платим Router'у) × курс `pricing.coins_per_usd` × наценка `pricing.markup` (T-000210) → нули (модель видима, бесплатна). Ручной override наценку НЕ применяет (= final). Закупку Router'а (COGS) сервер захватывает в `usage_stats` для админ-маржи, но клиенту не отдаёт.
- До поставки Router'ом **продажных (sell)** цен в consumer-каталог (REQ-002) авто-ветка пуста → каталог показывает ручной прайс; модели без него — нули.

**Errors:**
- `AI_NOT_CONFIGURED` (503) — Router не сконфигурирован (реестр пуст).
- `MODELS_FETCH_FAILED` (502) — каталог Router'а недоступен.

---

## GET /v1/api/balance/{provider}

Остаток **закупочного** баланса консумера у Router'а (агрегат `GET /v1/balance`
Swan_AiRouter — сумма по всем provider_account'ам). `{provider}` в пути игнорируется
с v0.40.0 (T-000148); ответ всегда под канон `SwanCode`. Семантика та же, что была у OpenRouter
credits: «сколько закупки осталось», НЕ баланс пользователя (тот — `GET /v1/api/me`).

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "provider": "SwanCode",
  "balance": 12.50,
  "currency": "USD",
  "asOf": "2026-06-11T10:00:00Z"
}
```

`asOf` — свежесть агрегата (RFC3339, omitempty — отсутствует до первого тика
balance-poller'а Router'а). Ответ `501 BALANCE_NOT_SUPPORTED` удалён в v0.33.0
(единственный провайдер баланс поддерживает).

---

## GET /v1/api/recommendations/{language}

Рекомендованные модели для языка/фреймворка. Возвращает курируемый список моделей, отсортированный по приоритету.

**Параметры пути:**

| Параметр | Тип | Описание |
|----------|-----|----------|
| language | string | Язык: `1c`, `go`, `python`, `javascript`, `flutter`, `csharp`, `java`, `kotlin`, `swift`, `rust`, `php`, `ruby`, `typescript`, `cpp` |

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "language": "1c",
  "recommendations": [
    {
      "modelId": "anthropic/claude-opus-4.6",
      "modelName": "Claude Opus 4.6",
      "reason": "Лучшее качество кода для 1С, отличная поддержка кириллицы"
    }
  ]
}
```

Если язык не найден — 200 с пустым массивом `recommendations: []`.

**Поля `recommendations[]`:**

| Поле | Тип | Описание |
|------|-----|----------|
| modelId | string | ID модели (как в GET /v1/api/models) |
| modelName | string | Человекочитаемое имя |
| reason | string | Обоснование рекомендации (omitempty) |

> Поле `provider` снято в v0.40.0 (T-000148): после cutover провайдер всегда канон `SwanCode`, рекомендация атрибутируется только моделью.

---

# Admin API (bearer `swan_ak_*`)

## GET /v1/admin/server-info

Идентификатор сервера для Test Connection в админ-клиенте и compatibility-check'а версии. Проверяет одним запросом: сервер живой, bearer-формат работает, admin-ключ валиден.

**Response (200):**
```json
{
  "serviceInfo": {"success": true},
  "version": "0.23.0",
  "name": "swancode_server",
  "uptime": 12345,
  "features": ["two-key-auth", "otp", "products", "admins", "feedback", "ai-router", "model-restriction", "model-display-overrides", "updates"]
}
```

| Поле | Тип | Описание |
|------|-----|----------|
| `version` | `string` | SemVer-версия сервера (`MAJOR.MINOR.PATCH`), source-of-truth — `internal/version/version.go` |
| `name` | `string` | Идентификатор сервера, стабильное имя репозитория |
| `uptime` | `int` | Секунды с момента старта процесса |
| `features` | `[string]` | Список feature-флагов текущей сборки. Hardcoded в `internal/transport/http/router.go` (`serverFeatures`), обновляется при добавлении/удалении возможностей |

**Errors:** только общие для admin-маршрутов (см. шапку файла, "Общие ошибки авторизации").

---

## GET /v1/admin/stats

Агрегированные метрики для дашборда админки.

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "usersCount": 42,
  "errorCount": 7
}
```

| Поле | Тип | Описание |
|------|-----|----------|
| usersCount | int | Активные пользователи (без soft-deleted) |
| errorCount | int | Записей в error_logs |

> `totalUserBalanceCoins` снят в v0.41.0 (F-000012): баланс ушёл в Swan_Billing, агрегата «сумма по всем кошелькам» у него нет (REQ-001 #6).

---

## GET /v1/admin/users/{id}/sessions

Список сессий конкретного пользователя с пагинацией.


**Query:** `?page=1&size=50`

**Response 200:** тот же формат что у `GET /v1/admin/sessions`.

**Response 404:** `USER_NOT_FOUND`

---

## GET /v1/admin/recommendations

Список всех рекомендаций моделей. Фильтрация по языку опциональна.


**Query:** `?language=1c` (опционально)

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "recommendations": [
    {
      "id": 1,
      "language": "1c",
      "modelId": "anthropic/claude-opus-4.6",
      "modelName": "Claude Opus 4.6",
      "rank": 1,
      "reason": "Лучшее качество кода для 1С",
      "isActive": true,
      "createdAt": "2026-04-12T00:00:00Z",
      "updatedAt": "2026-04-12T00:00:00Z"
    }
  ]
}
```

## POST /v1/admin/recommendations

Создание рекомендации.


**Request:**
```json
{
  "language": "go",
  "modelId": "anthropic/claude-opus-4.6",
  "modelName": "Claude Opus 4.6",
  "rank": 1,
  "reason": "Best for Go"
}
```
Обязательны `language` + `modelId`. Поле `provider` снято в v0.40.0 (T-000148).

**Response 201:** объект `recommendation` с присвоенным `id`.

## PUT /v1/admin/recommendations/{id}

Обновление рекомендации.

**Request:** все поля (без `provider`) + `isActive`.

**Response 200:** обновлённый объект `recommendation`.

## DELETE /v1/admin/recommendations/{id}

Удаление рекомендации.

**Response 200:** `{"success": true}`

---

## GET /v1/admin/prompts

Список промптов. Status: `implemented`.

**Query parameters:** `productId` — UUID продукта, опциональный.
- **Без параметра** (или пустое значение) — возвращает промпты **всех** продуктов одним списком (T-000061/REQ-023, breaking change в v0.20.2: до v0.20.1 в этом случае подсовывался default-продукт).
- **С `productId={uuid}`** — фильтр по конкретному продукту.

Сортировка: `ORDER BY product_name ASC, prompt_name ASC`.

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "prompts": [
    {
      "productId": "uuid",
      "productName": "default",
      "name": "project_analysis",
      "displayName": "Анализ проекта",
      "description": "Анализ проекта",
      "isActive": true,
      "updatedAt": "2026-03-27T12:00:00Z"
    }
  ]
}
```

Поле `productName` (T-000061/REQ-023, v0.20.2) — read-only имя продукта из таблицы `products`, добавлено чтобы админ-клиент рисовал группировку без второго запроса в `/v1/admin/products`.

Поле `displayName` (T-000063, v0.21.0) — человекочитаемое имя для заголовка карточки в UI. Поле `name` остаётся техническим идентификатором (используется в URL, миграциях, runtime fallback-lookup в `LoadPromptTemplate`). При пустом `displayName` админка должна делать fallback на `name`.

---

## GET /v1/admin/prompts/{name}

Детали одного промпта (включая шаблон). Status: `implemented`.

**Query parameters:** `productId` — UUID продукта, опциональный (default-продукт без параметра). Lookup строгий — БЕЗ автоматического фоллбека на default. Чтобы увидеть, какой шаблон подтянется в runtime для данного product+name (с фоллбеком), используется `Store.LoadPromptTemplate` при чате.

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "productId": "uuid",
  "name": "project_analysis",
  "displayName": "Анализ проекта",
  "description": "Анализ проекта",
  "template": "Сделай анализ о чем проект...",
  "defaultTemplate": "Сделай анализ о чем проект...",
  "isActive": true,
  "updatedAt": "2026-03-27T12:00:00Z"
}
```

Поле `defaultTemplate` (F-000010, v0.21.0) — дефолтный текст промпта из миграции. Если `template != defaultTemplate` — промпт правился вручную через PUT после деплоя. Админка показывает diff между ними и кнопку «Откатить к дефолту» (см. `POST /admin/prompts/{name}/reset`).

Поле `displayName` (T-000063, v0.21.0) — человекочитаемое имя; см. `GET /admin/prompts` для семантики.

**Errors:** `MISSING_NAME` (400), `PROMPT_NOT_FOUND` (404).

---

## PUT /v1/admin/prompts/{name}

Обновление промпта. Status: `implemented`.

**Query parameters:** `productId` — UUID продукта, опциональный (default-продукт без параметра).

**Request:**
```json
{
  "displayName": "Анализ проекта",
  "description": "Анализ проекта в целом",
  "template": "Новый текст шаблона..."
}
```

**Замечание про partial-update.** Все три поля **обязательны** в body: handler перезаписывает соответствующие колонки на присланные значения, включая пустую строку. Если клиент забудет `displayName` в JSON — оно станет пустым в БД. Колонка `default_template` (F-000010) не редактируется через эту ручку — её источник истины миграции. Для отката `template` к `default_template` используйте `POST /admin/prompts/{name}/reset`.

**Response 200:** `{"success": true}` (envelope).

**Errors:** `MISSING_NAME` (400), `INVALID_JSON` (400), `EMPTY_TEMPLATE` (400), `PROMPT_NOT_FOUND` (404), `DB_ERROR` (500).

---

## POST /v1/admin/prompts/{name}/reset

Сброс `template` к `default_template`. Status: `implemented`. (F-000010, v0.21.0)

Атомарный `UPDATE...RETURNING`. Идемпотентен: если `template` уже равен `default_template`, ничего семантически не меняется (только `updated_at` обновляется до `NOW()`). Системные промпты (`_system_*`) **разрешены** к сбросу — Delete-protection сюда не распространяется: сброс к дефолту безопасен по определению.

**Query parameters:** `productId` — UUID продукта, опциональный (default-продукт без параметра).

**Request:** body пустой (или игнорируется).

**Response 200:** полный `PromptDetailResponse` с актуальным состоянием — клиент может перерисовать форму без второго GET:
```json
{
  "serviceInfo": {"success": true},
  "productId": "uuid",
  "name": "_system_code_change",
  "displayName": "Инструменты консоли 1С",
  "description": "...",
  "template": "<новый = defaultTemplate>",
  "defaultTemplate": "<тот же>",
  "isActive": true,
  "updatedAt": "2026-05-20T15:42:33Z"
}
```

**Errors:** `MISSING_NAME` (400), `PROMPT_NOT_FOUND` (404), `DB_ERROR` (500).

---

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

`userId` / `userDisplayName` (v0.49.0, T-000183) приходят из `users`; fallback для display-name: `display_name` → `infraconnect_id` → `user_id` → отсутствие поля.

**`economics` (T-000210, админ-маржа, опционально):** агрегат экономики сессии — `chargedCoins` (списано с клиента, с наценкой) + `chargedUsd`/`chargedRub` (то же по **текущему** курсу `pricing.coins_per_usd`/`coins_per_rub`), `routerCostUsd`/`routerCostRub` (реальная закупка у Router'а, COGS) и `marginUsd`/`marginRub` = списание − закупка. Опускается (`omitempty`), если по сессии нет оплаченных ходов. **Только админ** — `routerCost*` (COGS) клиенту не отдаётся. Конвертация списания в $/₽ идёт по текущему курсу: если курс менялся с момента ходов, исторические значения приблизительны (live-вью). Те же поля — per-ход в `messages[].economics` (`GET /v1/admin/sessions/{id}/messages`) и в `GET /v1/admin/users/{id}/sessions`.

---

## GET /v1/admin/sessions/{id}/messages

Метаданные сессии + сообщения (включая `visible=false`). T-000060/REQ-022 в v0.20.2: фильтр `visible = TRUE` убран — админ-консоль диагностический инструмент и должна видеть полный tool-flow (assistant_tool_use, tool_result, любые системные служебные сообщения). Без query-параметров отдаётся старое поведение — **все** сообщения; с `limit`/`before` включается cursor-пагинация (v0.49.0, T-000182).

**Query (v0.49.0/v0.52.0):** `?limit=100&before=<messageId>&order=desc` — `limit` default 100, max 200; `before` выбирает сообщения с `id < before`. `order=desc|asc`, default `desc` (latest-first). Для следующей страницы в `desc`-режиме берите `before` равным последнему `id` текущей страницы; для `asc`-режима — первым `id` текущей страницы. Если `limit`/`before` не переданы, `hasMore=false` и возвращаются все сообщения в выбранном порядке.

**`?includeDeleted=true`** (v0.31.0): по умолчанию soft-deleted сессия → `404 SESSION_NOT_FOUND`; с флагом метаданные+сообщения отдаются (аудит).

**`session.systemPrompt`** (REQ-024 v0.23.2): полный текст системного промпта сессии (читается из `sessions.system_prompt`). Поле приходит всегда — пустая строка `""` для сессий, где промпт не был задан (без `omitempty`). Симметрично с листингом `/v1/admin/sessions`.

**`session.economics` + `messages[].economics`** (T-000210, админ-маржа): `session.economics` — агрегат по сессии, `messages[].economics` — маржа отдельного хода (присутствует у assistant-ходов с usage-строкой; опускается у user/tool/system-сообщений). Структура `TurnEconomics` — см. `GET /v1/admin/sessions`. Router-COGS (`routerCost*`) — только админу.

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
      "toolUses": [
        {"id": "toolu_abc", "name": "apply_code_change", "input": {"file": "...", "operation": "replace", "search": "...", "replace": "..."}}
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
- `toolUses` (`[]ToolUseBlock`, omitempty) — сырой payload из `messages.tool_uses` jsonb. Поля: `id` (string, для связки с tool_result), `name` (string, имя тула), `input` (any, аргументы тула).
- `toolResults` (`[]ToolResult`, omitempty) — сырой payload из `messages.tool_results` jsonb. Поля: `toolUseId` (string), `isError` (bool), `content` (string).
- `hasMore` (bool, v0.49.0) — есть более старые сообщения для следующей cursor-страницы. Для legacy-запроса без `limit`/`before` всегда `false`.

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

## GET /v1/admin/tables

Список таблиц в public schema с количеством строк.


**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "tables": [
    {"name": "error_logs", "rowCount": 12},
    {"name": "messages", "rowCount": 1234},
    {"name": "providers", "rowCount": 5},
    {"name": "sessions", "rowCount": 150},
    {"name": "users", "rowCount": 5}
  ]
}
```

**Изменение в v0.21.0 (T-000041):** `rowCount` теперь приближённое значение из `pg_class.reltuples` (один JOIN с `pg_class` вместо N синхронных `COUNT(*)`). После массовых INSERT/DELETE значение может расходиться с реальным до следующего `VACUUM`/`ANALYZE`. Точное значение остаётся в `GET /admin/tables/{name}` (через `COUNT(*)`).

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

# Admin: Skills — доменные knowledge-паки (F-000033, v0.61.0)

Skills — доменные знания, подгружаемые моделью **по требованию** (progressive disclosure): каталог `{name, description}` активных скиллов продукта инжектится в системный промпт (дёшево), тело модель тянет сама server-executed тулом `load_skill(name)` — результат оседает в истории диалога как `tool_result` (клиент вызова **не видит**, клиентский контракт не меняется). Повторная загрузка гасится dedup'ом tool_result (v0.53.0). Выключенный (`isActive=false`) скилл пропадает из каталога, `load_skill` по нему возвращает модели inline-ошибку `SKILL_NOT_FOUND` со списком доступных.

Хранение — таблица `skills` по модели промптов: `template` (живое тело, правится админом) + `defaultTemplate` (канон для diff/reset). Дефолты сидятся startup-reconcile'ом из файлов репо `skills_default/<product>/<name>.md`; `description` берётся из frontmatter файла и **reconcile-owned** — через PUT не редактируется. Стартовый набор — 3 консольных пака: `buh_skill`, `zup_skill`, `trade_skill`.

CRUD — калька с `/v1/admin/prompts`. Auth: `Authorization: Bearer swan_ak_*`.

## GET /v1/admin/skills

Список скиллов. Status: `implemented`.

**Query parameters:** `productId` — UUID продукта, опциональный. Без параметра — скиллы всех продуктов одним списком; с `productId={uuid}` — фильтр.

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "skills": [
    {
      "productId": "uuid",
      "productName": "query_console_1c",
      "name": "buh_skill",
      "description": "Запросы к бухгалтерским данным — ...",
      "isActive": true,
      "updatedAt": "2026-07-04T12:00:00Z"
    }
  ]
}
```

## GET /v1/admin/skills/{name}

Детали скилла (включая тело и дефолт). Status: `implemented`.

**Query parameters:** `productId` — UUID продукта, опциональный (default-продукт без параметра). Lookup строгий, без фоллбека.

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "productId": "uuid",
  "name": "buh_skill",
  "description": "Запросы к бухгалтерским данным — ...",
  "template": "# Skill: бухгалтерские запросы...",
  "defaultTemplate": "# Skill: бухгалтерские запросы...",
  "isActive": true,
  "updatedAt": "2026-07-04T12:00:00Z"
}
```

`template != defaultTemplate` → тело правилось вручную через PUT; админка показывает diff и «Откатить к дефолту».

**Errors:** `MISSING_NAME` (400), `SKILL_NOT_FOUND` (404).

## PUT /v1/admin/skills/{name}

Обновление **только тела** скилла. Status: `implemented`.

**Query parameters:** `productId` — опциональный.

**Request:**
```json
{"template": "Новый текст тела скилла..."}
```

`description` через эту ручку **не редактируется** (reconcile-owned: меняется правкой frontmatter в `skills_default/` + рестарт). `defaultTemplate` не редактируется (источник истины — файл).

**Response 200:** `{"success": true}` (envelope).

**Errors:** `MISSING_NAME` (400), `INVALID_JSON` (400), `EMPTY_TEMPLATE` (400), `SKILL_NOT_FOUND` (404), `DB_ERROR` (500).

## POST /v1/admin/skills

Создание ad-hoc скилла (мимо `skills_default/` — пак, который админ пишет прямо в админке). Status: `implemented`.

**Request:**
```json
{
  "productId": "uuid (optional, default → default-продукт)",
  "name": "custom_skill",
  "description": "Когда использовать (обязателен — уходит в каталог системного промпта)",
  "template": "Тело скилла..."
}
```

**Response 201:** `{"success": true}` (envelope). `default_template` инициализируется телом (diff нулевой).

**Errors:** `MISSING_NAME`/`EMPTY_TEMPLATE`/`MISSING_DESCRIPTION` (400), `SKILL_EXISTS` (409).

## DELETE /v1/admin/skills/{name}

Удаление скилла. Status: `implemented`. **Внимание:** удалённый reconcile-скилл (из `skills_default/`) вернётся на следующем рестарте — постоянное выключение делается через toggle.

**Query parameters:** `productId` — опциональный.

**Response 200:** `{"success": true}` (envelope). **Errors:** `SKILL_NOT_FOUND` (404).

## PATCH /v1/admin/skills/{name}/toggle

Переключение `isActive`. Status: `implemented`. Off → скилл пропадает из каталога системного промпта и из `load_skill`.

**Response 200:**
```json
{"serviceInfo": {"success": true}, "name": "buh_skill", "isActive": false}
```

## POST /v1/admin/skills/{name}/reset

Сброс `template` к `defaultTemplate` (атомарный `UPDATE...RETURNING`, идемпотентен). Status: `implemented`.

**Response 200:** полный detail-объект скилла (как `GET /admin/skills/{name}`).

**Errors:** `MISSING_NAME` (400), `SKILL_NOT_FOUND` (404), `DB_ERROR` (500).

---

## GET /v1/admin/config

Список runtime-настроек сервера (таблица `server_config`).


**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "entries": [
    {
      "key": "ai_tool_retry.max_attempts",
      "value": "2",
      "valueType": "int",
      "description": "Максимум автоматических retry при ошибках применения инструментов AI за один ассистент-ход"
    },
    {
      "key": "airouter.base_url",
      "value": "http://localhost:9091",
      "valueType": "string",
      "description": "Base URL Swan_AiRouter ..."
    },
    {
      "key": "airouter.consumer_key",
      "value": "***f48",
      "valueType": "string",
      "description": "Consumer-ключ к Swan_AiRouter (S2S). Маскируется в GET."
    },
    {
      "key": "swanupdates.base_url",
      "value": "https://upd.example.com",
      "valueType": "string",
      "description": "Base URL SwanUpdates ..."
    },
    {
      "key": "swanupdates.server_key",
      "value": "***1234",
      "valueType": "string",
      "description": "X-Server-Key SwanUpdates (S2S). Маскируется в GET."
    },
    {
      "key": "swanupdates.admin_key",
      "value": "***1234",
      "valueType": "string",
      "description": "Admin-ключ SwanUpdates для bootstrap server_key. Маскируется в GET."
    },
    {
      "key": "infraconnect.base_url",
      "value": "https://ic.example.com",
      "valueType": "string",
      "description": "Base URL Swan_InfraConnect ..."
    },
    {
      "key": "infraconnect.product_key",
      "value": "***1234",
      "valueType": "string",
      "description": "X-Product-Key к InfraConnect (S2S). Маскируется в GET."
    },
    {
      "key": "billing.base_url",
      "value": "https://billing.example.com",
      "valueType": "string",
      "description": "Base URL Swan_Billing ..."
    },
    {
      "key": "billing.server_key",
      "value": "***1234",
      "valueType": "string",
      "description": "X-Server-Key Swan_Billing (S2S). Маскируется в GET."
    },
    {
      "key": "querycrypt.key_grace_days",
      "value": "30",
      "valueType": "int",
      "description": "Grace QueryCrypt-ключа: дней после ротации retired-версия принимается как COMPONENT_OUTDATED, дальше ATTESTATION_INVALID. 0 = только current."
    },
    {
      "key": "attestation.enforce",
      "value": "observe",
      "valueType": "string",
      "description": "Режим проверки пломбы: observe | enforce. Гейт на query_console_1c."
    }
  ]
}
```

> `airouter.consumer_key`, `swanupdates.server_key`, `swanupdates.admin_key`, `infraconnect.product_key`, `billing.server_key` в листинге **маскируются** (`***`+last4) — полный ключ не возвращается. Сам ключ QueryCrypt-пломбы в `server_config` **не хранится** (живёт в таблице `querycrypt_keys`, управляется `/v1/admin/querycrypt/*`).
>
> `attestation.enforce` принимает только `observe` или `enforce` (enum-валидация при `PUT`); иное значение → `400 CONFIG_UPDATE_FAILED`. Смена `attestation.enforce` / `querycrypt.key_grace_days` применяется hot-reload без рестарта — пересобирает attestation-снапшот из живых ключей (F-000028).

---

## PUT /v1/admin/config

Обновление значения одной настройки. Ключ должен существовать в таблице — создание новых настроек делается через миграции.

**Request:**
```json
{
  "key": "ai_tool_retry.max_attempts",
  "value": "3"
}
```

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "entry": {
    "key": "ai_tool_retry.max_attempts",
    "value": "3",
    "valueType": "int",
    "description": "..."
  }
}
```

Изменения применяются сразу, без рестарта сервера. Значение валидируется по `valueType` (`int` \| `string` \| `bool`).

Поддерживаемые ключи:
- `ai_tool_retry.max_attempts` (int) — лимит retry при ошибках tool_use
- `airouter.base_url` (string, `v0.33.0`) — base URL микросервиса Swan_AiRouter (со схемой `http(s)://`). Источник истины конфига доступа к Router'у; env `AIROUTER_BASE_URL` — только разовый seed на первом старте. **Hot-reload:** при изменении RouterClient пересобирается без рестарта (новые chat-запросы идут на новый URL). Невалидная схема → `400 INVALID_AIROUTER_CONFIG`, значение НЕ сохраняется.
- `airouter.consumer_key` (string, `v0.33.0`) — consumer-ключ авторизации к Swan_AiRouter (S2S). Hot-reload как у `base_url`. **В `GET /v1/admin/config` значение маскируется** (`***`+last4; пустое → `(empty)`) — полный ключ в листинге не светится.
- `console1c.composite_type_threshold` (int, дефолт 5) — порог числа типов поля метаданных 1С, выше которого 1С-консоль схлопывает `type` поля в `{composite,typeCount}` вместо инлайн-массива. Сервер пушит значение в `input` каждого вызова `get_object_metadata` / `get_query_parameters` (поле `compositeTypeThreshold`). Hot-reload, без рестарта.
- `console1c.query_text_line_threshold` (int, дефолт 50, `v0.36.0`) — порог числа строк `queryText` в `get_current_query_text`, выше которого сервер не отдаёт модели полный текст, а возвращает блок `QUERY_TOO_LARGE`, направляя её к `get_query_tables` / `get_table_text`. Глобальный (server-side проверка, клиент не меняется). Hot-reload, без рестарта.
- `console1c.collection_render_cap` (int, дефолт 20, `v0.53.0`) — серверный backstop-потолок длины коллекций в ответах метадата-тулов `get_object_metadata` / `get_account_subkonto`: если клиент прислал коллекцию (предопределённые элементы/счета, виды субконто, список типов составного поля) развёрнутой с числом элементов > N, сервер принудительно схлопывает её в свёрнутую форму (`всего N (свёрнуто)` + drill-хинт) перед рендером в контекст AI. Backstop выше клиентского `composite_type_threshold` — для честного клиента невидим, ловит только модифицированный/сломанный. Глобальный (server-side, клиент не меняется). Hot-reload, без рестарта. ≤ 0 отключает.
- `console1c.c3_strict_apply` (bool, дефолт `false`, `v0.54.0`) — transition-safe cutover-гейт C3 (F-000018): как backfill закрывает висящий `apply_query_change`. `false` (переходное окно / старая консоль) → висящий apply = успех (`APPLY_OK`, без регрессии у модели); `true` (после релиза C3-консоли, всегда репортящей `applied`) → висящий apply = interrupted, `accepted` остаётся NULL. Резолв `accepted` из реального `applied`-репорта от флага не зависит. Глобальный, hot-reload.
- `console1c.server_tool_max_iters` (int, дефолт 3, `v0.54.0`) — кап server-tool-итераций (AI-вызовов на server-only ответах) за один ход (C3 server-loop, F-000018): защита от зацикливания модели на `get_latest_query_version`. Sync — счётчик цикла; async — пере-сабмиты между поллами. Глобальный, hot-reload. < 1 / нечисло → fallback 3.
- `pricing.coins_per_usd` (float, дефолт 200, `v0.40.0`) — **настоящий** курс коина к Router-USD: база коин-цены модели (dailyUSD-ветка EstimateCost) + показ трат клиенту (`costUsd = costCoins ÷ курс`). Глобальный, hot-reload. ≤ 0 / нечисло → fallback 200.
- `pricing.coins_per_rub` (float, дефолт 2, `v0.53.0`) — **настоящий** курс коина к рублю: показ трат клиенту (`costRub = costCoins ÷ курс`). Параллелен `coins_per_usd` (Router отдаёт и USD, и RUB). Глобальный, hot-reload. ≤ 0 / нечисло → fallback 2.
- `pricing.markup` (float, дефолт 1, `v0.53.0`) — наценка-множитель **в списании**: коины/ход = Router-sell × `coins_per_usd` × `markup`. Курс при этом настоящий — маржа живёт в этом множителе, не в курсе (показ клиенту делится на чистый курс, поэтому клиент видит наценённую трату, а не закупку). Применяется только к dailyUSD-ветке; ручные `model_prices` его игнорируют (= final). Дефолт 1 = без наценки (биллинг не меняется до настройки). Каталог моделей (`GET /v1/api/models`, `/v1/models/catalog`) показывает уже наценённую цену. Глобальный, hot-reload. ≤ 0 / нечисло → fallback 1.
- `swanupdates.base_url` (string, `v0.38.0`) — base URL микросервиса SwanUpdates (со схемой `http(s)://`). Источник истины конфига update-прокси; env `SWANUPDATES_BASE_URL` — только разовый seed на первом старте. **Hot-reload:** клиент пересобирается без рестарта, активный download-стрим доживает со своим снимком. Невалидная схема → `400 INVALID_SWANUPDATES_CONFIG`, не сохраняется. Пустой — выключает канал (эндпоинты → 503).
- `swanupdates.server_key` (string, `v0.38.0`) — X-Server-Key авторизации к SwanUpdates (S2S). Hot-reload как у `base_url`. **В `GET /v1/admin/config` маскируется** (`***`+last4; пустое → `(empty)`).
- `swanupdates.admin_key` (string, `v0.39.0`) — admin-ключ SwanUpdates для bootstrap `server_key`. Обычно задаётся не напрямую, а через `POST /v1/admin/swanupdates/bootstrap` (`persistAdminKey`), но допускается и прямой PUT. Без hot-reload (используется только в момент bootstrap). **В `GET /v1/admin/config` маскируется** (`***`+last4; пустое → `(empty)`).
- `swanupdates.cache_dir` / `swanupdates.cache_max_bytes` / `swanupdates.cache_max_age_hours` / `swanupdates.cache_cleanup_interval_minutes` / `swanupdates.cache_min_keep_per_product` (v0.52.1) — настройки consumer-side cache для `GET /v1/api/updates/download`. Cache включён, если `cache_dir` не пустой и не `disabled`; full download сохраняет artifact локально с singleflight по release, checksum-before-publish и retry/backoff на transport/429/5xx. Range/If-Range на cache-hit отдаётся из локального файла, cold Range остаётся прямым upstream passthrough. Cleanup ограничивает cache по TTL/size, сохраняя минимум новых artifact на product/platform.
- `infraconnect.base_url` (string, `v0.38.0`) — base URL микросервиса Swan_InfraConnect (со схемой `http(s)://`). Источник истины конфига OTP-логина + feedback; env `INFRACONNECT_BASE_URL` — только разовый seed. **Hot-reload:** клиент пересобирается без рестарта через общий holder (OTP-send/verify + feedback). Невалидная схема → `400 INVALID_INFRACONNECT_CONFIG`, не сохраняется. Пустой — выключает OTP/feedback (эндпоинты → 503).
- `infraconnect.product_key` (string, `v0.38.0`) — X-Product-Key авторизации к Swan_InfraConnect (S2S). Hot-reload как у `base_url`. **В `GET /v1/admin/config` маскируется** (`***`+last4; пустое → `(empty)`).
- `infraconnect.validate_grace_seconds` (int, дефолт 600, `v0.56.3`) — grace-окно валидации per-user ключа при **недоступности** InfraConnect (F-000029): сглаживает моргания инфры поверх обычного fresh-TTL кэша. Когда round-trip к IC падает по недоступности (сеть / 5xx / нераспознанный код), недавно успешно проверенный ключ ещё принимается в течение этого окна (вместо `503 KEY_VALIDATION_UNAVAILABLE`). На типизированный негатив (`USER_API_KEY_INVALID`/`_EXPIRED`/`_PROJECT_MISMATCH`/`USER_DEACTIVATED`) grace **не** распространяется — отзыв/деактивация действуют сразу, запись выселяется. Grace ограничен сверху hard-expiry самого ключа. Глобальный, hot-reload. `0` = grace выключен; < 0 / нечисло → fallback 600.
- `billing.base_url` (string, `v0.41.0`) — base URL микросервиса Swan_Billing (со схемой `http(s)://`). Источник истины конфига доступа к биллингу; env `BILLING_BASE_URL` — только разовый seed на первом старте. **Hot-reload:** S2S-клиент пересобирается без рестарта через общий holder (money-path + admin read-proxy). Невалидная схема → `400 INVALID_BILLING_CONFIG`, не сохраняется. Пустой — money-path → `503 BILLING_NOT_CONFIGURED`.
- `billing.server_key` (string, `v0.41.0`) — X-Server-Key авторизации к Swan_Billing (S2S). Hot-reload как у `base_url`. **В `GET /v1/admin/config` маскируется** (`***`+last4; пустое → `(empty)`).

**Errors (config hot-reload):**
- `INVALID_AIROUTER_CONFIG` (400) — `airouter.base_url` без схемы `http(s)://`. Значение не персистится, работающий RouterClient не тронут.
- `INVALID_SWANUPDATES_CONFIG` (400) — `swanupdates.base_url` без схемы `http(s)://`. Значение не персистится, работающий update-клиент не тронут.
- `INVALID_INFRACONNECT_CONFIG` (400) — `infraconnect.base_url` без схемы `http(s)://`. Значение не персистится, работающий InfraConnect-клиент не тронут.
- `INVALID_BILLING_CONFIG` (400) — `billing.base_url` без схемы `http(s)://`. Значение не персистится, работающий Billing-клиент не тронут.

> Пустые `airouter.*` (БД + env) → сервер в деградации: chat/models/balance → `503 AI_NOT_CONFIGURED`. Пустые `swanupdates.*` → update-эндпоинты → `503 UPDATES_NOT_CONFIGURED` (канал безопасно спит). Пустой `infraconnect.base_url` → OTP/feedback → `503 INFRACONNECT_NOT_CONFIGURED`. Пустой `billing.base_url` → money-path (chat/tool-retry/tool-results) → `503 BILLING_NOT_CONFIGURED` (hard-dependency).
>
> При **недоступности** (а не отсутствии конфига) InfraConnect валидация per-user ключа сначала пытается grace-фолбэк (`infraconnect.validate_grace_seconds`, дефолт 10 мин) на недавно проверенный ключ, и только если grace не покрывает — отвечает `503 KEY_VALIDATION_UNAVAILABLE`. Это сглаживает короткие моргания IC, не пуская их в клиент.

---

## GET /v1/admin/microservices/health

Агрегированная проверка состояния микросервисов (v0.52.0, REQ-031). Использует текущий `server_config`, кэширует результат на 30 секунд; `?refresh=true` принудительно обновляет кэш.

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "status": "degraded",
  "checkedAt": "2026-06-26T12:00:00Z",
  "cacheTtlSec": 30,
  "items": [
    {"name": "ai-router", "status": "online", "httpStatus": 200},
    {"name": "billing", "status": "not_configured", "errorCode": "BILLING_NOT_CONFIGURED"}
  ]
}
```

`status` и `items[].status`: `online`, `degraded`, `offline`, `not_configured`. Детальная причина остаётся в `errorCode`/`message`; для сырого `ok|auth_failed|unreachable|bad_config|unexpected_status` используйте точечный `POST /v1/admin/microservices/{name}/check`.

---

## POST /v1/admin/microservices/{name}/check

Проверка доступности и ключа микросервиса без сохранения значений в `server_config` (v0.49.0, T-000184 / REQ-029). `{name}`: `ai-router`, `billing`, `infraconnect`, `swanupdates` (поддерживаются также legacy aliases).

**Request (optional):**
```json
{
  "baseUrl": "https://router.example.com",
  "consumerKey": "swan_consumer_...",
  "serverKey": "swan_srv_...",
  "productKey": "swan_pk_...",
  "product": "lensa-console",
  "currentVersion": "v0.49.0"
}
```

Неуказанные поля берутся из текущего `server_config`. Для AI Router используется `consumerKey`, для Billing/SwanUpdates — `serverKey`, для InfraConnect — `productKey`.

**Response 200/400:**
```json
{
  "serviceInfo": {"success": true},
  "name": "ai-router",
  "ok": true,
  "status": "ok",
  "httpStatus": 200,
  "errorCode": ""
}
```

`status`: `ok`, `auth_failed`, `unreachable`, `bad_config`, `unexpected_status`. `bad_config` возвращается с HTTP 400, остальные статусы — HTTP 200, чтобы админка могла показать результат проверки без исключения transport-ошибки.

---

## POST /v1/admin/swanupdates/bootstrap

Провижн `swanupdates.server_key` по admin-ключу SwanUpdates (`v0.39.0`, REQ-030). У SwanUpdates нет ручки «отдать существующий server_key» (он показывается один раз — при создании/ротации trusted-server), поэтому сервер **провижнит**: ищет свой trusted-server по фиксированному имени `swancode_server` и ротирует его ключ (`POST /v1/admin/servers/{id}/regenerate-key`), либо создаёт (`POST /v1/admin/servers`). Полученный одноразовый ключ персистится в `swanupdates.server_key` с hot-reload (как `PUT /v1/admin/config`). Admin-ключ SwanUpdates пользователю не возвращается.

**Request:**
```json
{
  "adminKey": "string, optional — admin-ключ SwanUpdates; пустое тело → re-bootstrap из сохранённого swanupdates.admin_key",
  "persistAdminKey": "bool, optional, default true — сохранить ли adminKey в swanupdates.admin_key для re-bootstrap"
}
```

**Response (200) — успех:**
```json
{
  "serviceInfo": {"success": true},
  "ok": true,
  "serverKeyMasked": "***1234",
  "adminKeyPersisted": true
}
```

**Response (200) — admin-ключ отвергнут SwanUpdates (мягкая ошибка):**
```json
{
  "serviceInfo": {"success": true},
  "ok": false,
  "errorCode": "SWANUPDATES_AUTH_FAILED",
  "errorMessage": "SwanUpdates отверг admin-ключ"
}
```
`ok:false` при `success:true` (HTTP 200) — чтобы админка отличила «ключ битый» (правь поле) от сетевой недоступности (жди сервис) и показала результат inline.

**Errors (envelope success:false):**
- `INVALID_ADMIN_KEY` (400) — `adminKey` передан пустым
- `ADMIN_KEY_NOT_STORED` (404) — re-bootstrap (пустое тело), но `swanupdates.admin_key` не сохранён
- `SWANUPDATES_NOT_CONFIGURED` (503) — пустой `swanupdates.base_url`, неоткуда bootstrap'ить
- `UPDATES_NOT_CONFIGURED` (503) — update-подсистема не активна на инстансе
- `SWANUPDATES_UPSTREAM_ERROR` (502) — сеть/таймаут до SwanUpdates или нераспознанный ответ
- `CONFIG_UPDATE_FAILED` (500) — ключ получен, но не сохранён (повторите bootstrap)

**Side effects:**
- Создание/ротация trusted-server на стороне SwanUpdates — при ротации старый `server_key` немедленно инвалидируется.
- `swanupdates.server_key` персистится + hot-reload SwanUpdates-клиента (без рестарта).
- При `persistAdminKey=true` и `adminKey` из тела — `swanupdates.admin_key` персистится (маскируется в `GET /v1/admin/config`).

---

## POST /v1/admin/swanupdates/test-download

Функциональная проверка SwanUpdates-канала (v0.52.0, REQ-032): сервер делает upstream `check`, берёт `releaseId`, затем читает небольшой диапазон артефакта через `download`. Это проверяет не только ключи, но и достижимость манифеста/файла. Если у выбранного продукта нет опубликованного релиза, проба ищет любой другой активный продукт с релизом и качает сэмпл с него (v0.56.2, REQ-039 variant B) — чтобы реально проверить download, а не упереться в «релиза нет».

**Request (optional):**
```json
{
  "product": "lensa-console",
  "productId": "uuid",
  "platform": "windows",
  "currentVersion": "v0.0.0",
  "maxBytes": 65536
}
```

`product` можно указать напрямую. Если передан `productId`, сервер возьмёт `products.updates_product_name`. Если продукт не указан и в каталоге ровно один активный продукт с `updatesProductName`, он будет выбран автоматически; иначе вернётся `400 PRODUCT_REQUIRED`. `maxBytes` по умолчанию 64 KiB, максимум 1 MiB.

**Response 200 (релиз есть):**
```json
{
  "serviceInfo": {"success": true},
  "ok": true,
  "channelOk": true,
  "releaseFound": true,
  "product": "lensa-console",
  "check": {
    "updateAvailable": true,
    "latestVersion": "v0.52.0",
    "releaseId": "rel_...",
    "fileSize": 12345678,
    "sha256": "..."
  },
  "download": {
    "httpStatus": 206,
    "contentType": "application/octet-stream",
    "contentLength": 65536,
    "sampledBytes": 65536,
    "rangeSupported": true,
    "checksumSha256": "..."
  }
}
```

**Response 200 (релиз нашёлся у другого продукта — v0.56.2, REQ-039 fallback):**
```json
{
  "serviceInfo": {"success": true},
  "ok": true,
  "channelOk": true,
  "releaseFound": true,
  "product": "other-product",
  "requestedProduct": "lensa-console",
  "check": { "latestVersion": "...", "releaseId": "..." },
  "download": { "httpStatus": 206, "sampledBytes": 65536, "rangeSupported": true }
}
```

**Response 200 (канал жив, релиза нет ни у кого — v0.56.2, REQ-039):**
```json
{
  "serviceInfo": {"success": true},
  "ok": true,
  "channelOk": true,
  "releaseFound": false,
  "product": "lensa-console",
  "check": {},
  "download": {},
  "errorCode": "RELEASE_NOT_FOUND",
  "errorMessage": "Канал работает, но release не опубликован ни для одного продукта"
}
```

- `channelOk` (v0.56.2) — S2S-auth прошёл и manifest-endpoint достижим (канал исправен).
- `releaseFound` (v0.56.2) — где-то есть опубликованный релиз и сэмпл реально скачан. `false` → канал жив, но скачивать нечего ни у одного продукта; блоки `check`/`download` пустые, ориентироваться на `channelOk`.
- `product` — продукт, с которого реально скачан сэмпл. `requestedProduct` (v0.56.2, REQ-039 variant B) — присутствует только если у выбранного продукта релиза не было и проба сделана на другом; UI может пояснить «у X релиза нет, проверено на Y».
- **Изменение семантики (v0.56.2, REQ-039):** upstream `RELEASE_NOT_FOUND` у выбранного продукта больше **не** возвращается как `404`. Сначала ищется другой активный продукт с релизом (сэмпл качается с него, `releaseFound:true`); если релиза нет нигде — `200` с `channelOk:true, releaseFound:false`. Проб тестирует **канал и download**, а не наличие релиза у конкретного продукта. Прочие провалы канала (`INVALID_SERVER_KEY` и т.п.) по-прежнему ошибки (502).

**Errors:** `UPDATES_NOT_CONFIGURED`/`SWANUPDATES_NOT_CONFIGURED` (503), `PRODUCT_REQUIRED` (400), `UPDATES_NOT_AVAILABLE` (404), `SWANUPDATES_UPSTREAM_ERROR` (502). `RELEASE_NOT_FOUND` на `check` больше не ошибка (см. выше).

---

## POST /v1/admin/querycrypt/rotate

Генерит новую версию ключа QueryCrypt-пломбы, демоутит прошлую current → retired и возвращает **one-time-reveal** plaintext seed/mask (для сборки dll — нигде больше не достать). Пересобирает attestation-снапшот (новая current + прошлая retired в grace). F-000028.

**Request:**
```json
{
  "confirm": "bool, required — должно быть true (guard двойной валидации; ротация необратимо ломает пломбы парка вне grace-окна)"
}
```

**Response (200):**
```json
{
  "serviceInfo": "(standard)",
  "version": "int — номер новой current-версии",
  "seedHex": "string — 64 hex, plaintext (REVEAL, показывается единожды)",
  "maskHex": "string — 64 hex, plaintext (REVEAL)",
  "createdAt": "RFC3339 timestamp"
}
```

**Errors:**
- `CONFIRM_REQUIRED` (400) — тело без `confirm:true`
- `ATTESTATION_NOT_CONFIGURED` (503) — подсистема attestation не активна на инстансе
- `QUERYCRYPT_ROTATE_FAILED` (500) — не удалось сгенерировать/записать ключ
- `ATTESTATION_RELOAD_FAILED` (500) — ключ создан, но снапшот не пересобран (требуется рестарт)

**Side effects:**
- INSERT новой current в `querycrypt_keys`, прошлая current → `retired` (`retired_at=NOW()`).
- Hot-reload attestation-снапшота (живые версии пересобираются без рестарта).

---

## GET /v1/admin/querycrypt/keys

История версий ключа пломбы (current-первой). seed/mask **маскированы** (`***`+last4) — plaintext только в reveal ротации. F-000028.

**Response (200):**
```json
{
  "serviceInfo": "(standard)",
  "graceDays": "int — текущее querycrypt.key_grace_days",
  "keys": [
    {
      "version": "int",
      "status": "current | retired",
      "seedMasked": "string — ***+last4",
      "maskMasked": "string — ***+last4",
      "createdAt": "RFC3339",
      "retiredAt": "RFC3339, omitempty (только у retired)",
      "inGrace": "bool — retired-версия ещё принимается как COMPONENT_OUTDATED"
    }
  ]
}
```

**Errors:**
- `ATTESTATION_NOT_CONFIGURED` (503) — подсистема attestation не активна на инстансе

---

## Удалено в v0.33.0: GET/PUT /v1/admin/providers*, PUT/DELETE /v1/admin/models/{provider}/{modelId}

Cutover F-000007: ключи, прокси и активность провайдеров управляются в админке самого Swan_AiRouter; серверная таблица `providers` снесена (миграция 017). Price-override'ы с осью `{provider}` заменены поверхностью внутреннего прайса `/v1/admin/model-prices` (ниже).

---

## GET /v1/admin/model-prices

Весь внутренний прайс моделей (источник биллинга) + **продажные (sell)** цены Router'а для контроля маржи.

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "prices": [
    {
      "modelId": "anthropic/claude-sonnet-4.6",
      "displayName": "Claude Sonnet 4.6",
      "promptPriceCoins": 0.005,
      "completionPriceCoins": 0.020,
      "sellInputPer1m": 3300000,
      "sellOutputPer1m": 16500000
    }
  ]
}
```

Поля:
- `promptPriceCoins` / `completionPriceCoins` — наша цена, **коины за токен** (float). Источник Hold/Settle биллинга и цен в `GET /v1/api/models`.
- `sellInputPer1m` / `sellOutputPer1m` — **продажная** ставка Router'а = что **мы платим** Router'у (его закупка + наценка), **micro-USD за 1M токенов**. Это наша себестоимость для контроля маржи (revenue в коинах vs sell), НЕ закупка Router'а у апстрима. (`omitempty`: отсутствуют, пока Router не отдаёт sell в consumer-каталоге (REQ-002) или модель не найдена).
- `displayName` — из Router-каталога или admin override `model_display_overrides` (`omitempty`).

---

## PUT /v1/admin/model-prices/{modelId}

Установка/обновление внутренней цены модели. `{modelId}` — канонический ID `vendor/name`, путь матчится **wildcard'ом** (слеши в ID передаются как есть, URL-encoding не требуется): `PUT /v1/admin/model-prices/anthropic/claude-sonnet-4.6`.

**Request:**
```json
{
  "promptPriceCoins": 0.005,
  "completionPriceCoins": 0.020
}
```

Оба поля обязательны, ≥ 0, коины за токен.

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "modelId": "anthropic/claude-sonnet-4.6",
  "promptPriceCoins": 0.005,
  "completionPriceCoins": 0.020
}
```

**Ошибки:**
- 400 `MISSING_PARAMS` — пустой modelId.
- 400 `INVALID_REQUEST` — не указан `promptPriceCoins` или `completionPriceCoins`.
- 400 `INVALID_PRICE` — отрицательное значение.

**Side effects:** `Registry.Reload()` — следующий ход уже биллится по новой цене.

---

## DELETE /v1/admin/model-prices/{modelId}

Снятие цены. Модель остаётся видимой в каталоге с нулевой ценой; её ходы **не тарифицируются** (fail-open, в логе warn `BILLING_SKIPPED`).

**Response 200:** `{"success": true}` + `Registry.Reload()`.

---

## GET /v1/admin/model-display-overrides

Список admin-defined labels моделей (v0.49.0, T-000198). Override меняет только человекочитаемый `name` в `GET /v1/api/models` и `displayName` в `GET /v1/admin/model-prices`; технический `modelId` не меняется.

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "overrides": [
    {
      "modelId": "openai/gpt-5",
      "displayName": "GPT-5",
      "createdAt": "2026-06-26T10:00:00Z",
      "updatedAt": "2026-06-26T10:00:00Z"
    }
  ]
}
```

---

## GET /v1/admin/model-display-overrides/{modelId}

Чтение override одной модели. `{modelId}` — wildcard `vendor/name`.

**Response 200:** `{ "serviceInfo": {"success": true}, "override": { ... } }`

**Errors:** `404 MODEL_DISPLAY_OVERRIDE_NOT_FOUND`.

---

## PUT /v1/admin/model-display-overrides/{modelId}

Создание/обновление display-name override.

**Request:**
```json
{
  "displayName": "Claude Sonnet 4.6"
}
```

`displayName` trim'ится, допустимая длина — 1..200 символов.

**Response 200:** `{ "serviceInfo": {"success": true}, "override": { ... } }`

**Errors:** `400 INVALID_JSON`, `400 INVALID_DISPLAY_NAME`.

---

## DELETE /v1/admin/model-display-overrides/{modelId}

Удаление override. После удаления `GET /v1/api/models` снова отдаёт имя из Router-каталога.

**Response 200:** `{"success": true}`

---

## DELETE /v1/admin/database/reset

Пересоздание БД (только для разработки). Удаляет все таблицы и применяет миграции заново.

**Response 200:**
```json
{
  "serviceInfo": {"success": true}
}
```

---

## GET /v1/admin/admins

Список всех активных (не-deleted) adminов.

**Response (200):**
```json
{
  "serviceInfo": {"success": true},
  "items": [
    {
      "id": "uuid",
      "login": "string",
      "createdAt": "RFC3339"
    }
  ]
}
```

**Errors:**
- `DB_ERROR` (500) — ошибка базы данных

---

## POST /v1/admin/admins

Создание нового админа. Сервер автоматически генерирует первый admin-ключ.

**Request:**
```json
{
  "login": "string, required — уникальный логин нового админа"
}
```

**Response (200):**
```json
{
  "serviceInfo": {"success": true},
  "admin": {
    "id": "uuid",
    "login": "string",
    "createdAt": "RFC3339"
  },
  "key": "swan_ak_{prefix}_{secret} — первый ключ, выдаётся ОДИН раз",
  "keyId": "uuid — ID ключа (нужен для DELETE /v1/admin/admin-keys/{id})"
}
```

**Errors:**
- `INVALID_JSON` (400) — невалидный JSON
- `INVALID_REQUEST` (400) — login пуст
- `ADMIN_LOGIN_TAKEN` (409) — логин уже занят
- `INTERNAL_ERROR` (500) — ошибка генерации ключа
- `DB_ERROR` (500) — ошибка базы данных

**Side effects:**
- INSERT в `admins`
- INSERT в `admin_keys` (label=`initial`)

---

## POST /v1/admin/admins/me/keys

Выпустить новый admin-ключ для себя (текущего аутентифицированного админа).

**Request:** тело опциональное.
```json
{
  "label": "string, opt — операторская метка ключа"
}
```

**Response (200):**
```json
{
  "serviceInfo": {"success": true},
  "keyId": "uuid",
  "key": "swan_ak_{prefix}_{secret} — выдаётся ОДИН раз",
  "label": "string",
  "createdAt": "RFC3339"
}
```

**Errors:**
- `INTERNAL_ERROR` (500) — ошибка генерации ключа
- `DB_ERROR` (500) — ошибка сохранения ключа

**Side effects:**
- INSERT в `admin_keys`
- Если target-admin имеет active bootstrap-row (`key_prefix='__bootstrap__'`) — она помечается revoked (`revoked_at = NOW()`) в той же tx что и INSERT нового ключа.
- Audit `key.issued` для нового ключа.
- Если bootstrap-row была revoked — дополнительно audit `key.revoked` с `metadata.reason="bootstrap_replaced"`.

---

## GET /v1/admin/admins/me/keys

Список всех ключей текущего аутентифицированного админа (включая отозванные).

**Response (200):**
```json
{
  "serviceInfo": {"success": true},
  "items": [
    {
      "id": "uuid",
      "keyPrefix": "8 hex chars",
      "label": "string",
      "createdAt": "RFC3339",
      "revoked": false
    }
  ]
}
```

**Errors:**
- `DB_ERROR` (500) — ошибка базы данных

---

## POST /v1/admin/admins/{id}/keys

Выпустить admin-ключ для другого админа (peer-admin power).

**Request:** тело опциональное.
```json
{
  "label": "string, opt"
}
```

**Response (200):** тот же формат что у `POST /v1/admin/admins/me/keys`.

**Errors:**
- `INVALID_REQUEST` (400) — id пуст
- `ADMIN_NOT_FOUND` (404) — админ не найден
- `INTERNAL_ERROR` (500) — ошибка генерации ключа
- `DB_ERROR` (500) — ошибка базы данных

**Side effects:**
- INSERT в `admin_keys`
- Если target-admin имеет active bootstrap-row (`key_prefix='__bootstrap__'`) — она помечается revoked (`revoked_at = NOW()`) в той же tx что и INSERT нового ключа.
- Audit `key.issued` для нового ключа.
- Если bootstrap-row была revoked — дополнительно audit `key.revoked` с `metadata.reason="bootstrap_replaced"`.

---

## Удалено в v0.33.0: POST /v1/admin/_bootstrap/reset

Одноразовый migration tool (DEPRECATED-at-birth с v0.24.2) снесён (T-000085). Штатный recovery утерянного admin-доступа: `psql "DELETE FROM admins"` + рестарт сервера с новым `ADMIN_API_KEY` (BootstrapFirstAdmin пересоздаст admin'а; см. `docs/flows/admin-auth.md`).

---

## DELETE /v1/admin/admin-keys/{id}

Отозвать admin-ключ по ID. Любой аутентифицированный админ может отозвать любой ключ.

**Response (200):**
```json
{
  "serviceInfo": {"success": true}
}
```

**Errors:**
- `INVALID_REQUEST` (400) — id пуст
- `KEY_NOT_FOUND` (404) — ключ не найден
- `DB_ERROR` (500) — ошибка базы данных

**Side effects:**
- UPDATE `admin_keys SET revoked_at = NOW()`; с этого момента ключ не принимается middleware

---

## POST /v1/admin/users/{id}/keys

Выпустить user-ключ для пользователя (admin-issued, label=`admin-issued`).

**Response (200):**
```json
{
  "serviceInfo": {"success": true},
  "keyId": "uuid",
  "key": "swan_uk_{prefix}_{secret} — выдаётся ОДИН раз",
  "createdAt": "RFC3339"
}
```

**Errors:**
- `MISSING_ID` (400) — id пуст
- `USER_NOT_FOUND` (404) — пользователь не найден
- `INTERNAL_ERROR` (500) — ошибка генерации ключа
- `DB_ERROR` (500) — ошибка сохранения ключа

**Side effects:**
- INSERT в `user_keys` (label=`admin-issued`)

---

## GET /v1/admin/users/{id}/keys

Список ключей конкретного пользователя для admin-панели (REQ-019). Симметричен `GET /v1/admin/products/{id}/keys`. Включает active + revoked. `lastUsedAt` агрегируется из `user_auth_events` (`event='auth.success'` WHERE `key_prefix=uk.key_prefix`) через LEFT JOIN — использует существующий partial index `idx_user_auth_events_key_prefix`. Soft-deleted юзеры возвращают свои ключи (404 только для несуществующих).

**Request:** `Authorization: Bearer swan_ak_*`. Body пустой.

**Response (200):**
```json
{
  "serviceInfo": {"success": true},
  "keys": [
    {
      "id":         "uuid",
      "keyPrefix":  "string — 8 hex chars, публичная часть swan_uk_{prefix}_*",
      "label":      "string — admin-issued | otp | произвольная",
      "lastUsedAt": "RFC3339 timestamp | '' если ни разу не использовался или только через GET",
      "revokedAt":  "RFC3339 timestamp | '' если ключ активен",
      "createdAt":  "RFC3339 timestamp"
    }
  ]
}
```

Поля `lastUsedAt` и `revokedAt` **всегда присутствуют** в JSON, пустая строка `""` когда не применимо (REQ-019 контракт — отступление от общей `omitempty`-конвенции).

**Errors:**
- `MISSING_ID` (400) — `id` пуст
- `USER_NOT_FOUND` (404) — пользователь с таким `id` не существует
- `DB_ERROR` (500) — ошибка чтения

**Сортировка:** active first (`revoked_at IS NULL` DESC), затем `created_at DESC`.

**Note про `lastUsedAt`:** Server эмитит `auth.success` в `user_auth_events` только для не-GET HTTP-методов (T-000047 — чтобы не засирать аудит-лог чтениями). Поэтому ключ, через который ходили только GET'ом, имеет `lastUsedAt: ""`. Для cleanup-кейса (поиск забытых ключей) это работает корректно: пустой `lastUsedAt` = «никогда не использовался для write-операций».

**Side effects:** нет (read-only).

---

## DELETE /v1/admin/user-keys/{id}

Отозвать user-ключ по ID.

**Response (200):**
```json
{
  "serviceInfo": {"success": true}
}
```

**Errors:**
- `MISSING_ID` (400) — id пуст
- `KEY_NOT_FOUND` (404) — ключ не найден
- `DB_ERROR` (500) — ошибка отзыва ключа

**Side effects:**
- UPDATE `user_keys SET revoked_at = NOW()`

---

## GET /v1/admin/admin-actions

Список действий админов (audit log) с фильтрами и пагинацией. Только для админов (`swan_ak_*`). Append-only — удаление возможно только через retention goroutine (см. `AUDIT_RETENTION_DAYS`).

**Request:** query-параметры (все опциональны)
- `action` (csv) — одно или несколько значений из vocabulary (`auth.success`, `auth.fail`, `admin.created`, `admin.bootstrap_created`, `admin.key_issued`, `admin.key_revoked`, `user.created`, `user.deleted`, `user.balance_changed`, `user.key_issued`, `user.key_revoked`, `product.created`, `product.updated`, `product.deleted`, `product.key_issued`, `product.key_revoked`)
- `admin_id` (uuid) — фильтр по конкретному админу
- `target_kind` (enum: `admin` | `user` | `product` | `key`) — тип целевой сущности
- `target_id` (uuid) — id целевой сущности
- `key_prefix` (string, exact match) — фильтр по prefix ключа (для key.* событий)
- `since`, `until` (RFC3339) — временной диапазон
- `page` (int, default `1`)
- `size` (int, default `50`, max `200`)

**Response (200):**
```json
{
  "serviceInfo": "(standard)",
  "items": [
    {
      "id": "uuid",
      "adminId": "uuid",
      "action": "user.balance_changed",
      "targetKind": "user",
      "targetId": "uuid, optional",
      "keyPrefix": "string, optional",
      "ip": "string, optional",
      "userAgent": "string, optional",
      "metadata": {"delta_coins": 100},
      "createdAt": "RFC3339 timestamp"
    }
  ],
  "total": "int — общее число записей под фильтром",
  "page": "int",
  "size": "int"
}
```

**Errors:**
- `INVALID_REQUEST` (400) — неизвестный action, невалидный RFC3339 в since/until, size > 200
- `DB_ERROR` (500) — ошибка чтения

---

## GET /v1/admin/user-auth-events

Список auth-событий юзеров и продуктов (audit log) с фильтрами и пагинацией. Только для админов (`swan_ak_*`). Append-only.

**Request:** query-параметры (все опциональны)
- `event` (csv) — одно или несколько значений из vocabulary (`auth.success`, `auth.fail`, `otp.sent`, `otp.verified`, `key.issued`)
- `user_id` (uuid)
- `product_id` (uuid)
- `key_prefix` (string, exact match)
- `since`, `until` (RFC3339)
- `page` (int, default `1`)
- `size` (int, default `50`, max `200`)

**Response (200):**
```json
{
  "serviceInfo": "(standard)",
  "items": [
    {
      "id": "uuid",
      "event": "otp.verified",
      "userId": "uuid, optional",
      "productId": "uuid, optional",
      "keyPrefix": "string, optional",
      "ip": "string, optional",
      "userAgent": "string, optional",
      "metadata": {"phone": "+7..."},
      "createdAt": "RFC3339 timestamp"
    }
  ],
  "total": "int",
  "page": "int",
  "size": "int"
}
```

**Errors:**
- `INVALID_REQUEST` (400) — неизвестный event, невалидный RFC3339 в since/until, size > 200
- `DB_ERROR` (500) — ошибка чтения

---

## GET /v1/admin/error-logs

Список записей `error_logs` (серверные ошибки/warn — в т.ч. дословный ответ апстримов вроде Billing/Router в `context`) с фильтрами и пагинацией. Только для админов (`swan_ak_*`). Read-only; запись делает сам сервер в точках ошибок. Диагностика дрейфа каталога и сбоев money-path: настоящий код апстрима, завёрнутый в наш `500`, виден здесь в `context` (T-000149).

**Request:** query-параметры (все опциональны)
- `level` (enum: `error` / `warn` / `info`) — точное совпадение
- `userId` (uuid, v0.50.0) — точное совпадение по `error_logs.user_id`
- `code` (string) — ILIKE-подстрока (`?code=BILLING` ловит `BILLING_ERROR`, `BILLING_NOT_CONFIGURED`)
- `message` (string) — ILIKE-подстрока
- `since`, `until` (RFC3339)
- `page` (int, default `1`)
- `size` (int, default `50`, max `200`)

**Response (200):**
```json
{
  "serviceInfo": "(standard)",
  "items": [
    {
      "id": "int",
      "userId": "uuid, optional",
      "level": "error",
      "code": "BILLING_ERROR",
      "message": "reserve: ...",
      "context": {"handler": "chat", "billingStatus": "400", "...": "..."},
      "createdAt": "RFC3339 timestamp"
    }
  ],
  "total": "int",
  "page": "int",
  "size": "int"
}
```

Записи отсортированы по `created_at DESC` (новые сверху). `context` — произвольный JSON-объект (может быть `{}`).

**Errors:**
- `INVALID_REQUEST` (400) also covers invalid `userId` (v0.50.0).
- `INVALID_REQUEST` (400) — неизвестный level, невалидный RFC3339 в since/until
- `DB_ERROR` (500) — ошибка чтения

`error_logs.user_id` (v0.50.0, T-000149) заполняется из request/error context, когда доступен локальный `users.id`, и связан с `users(id) ON DELETE SET NULL`.

---

## POST /v1/feedback

Отправить feedback (отзыв) пользователя. Proxy в InfraConnect. Требует `Authorization: Bearer swan_uk_*` + `X-Product-Key: swan_pk_*`.
Пользователь должен быть привязан к InfraConnect (прошёл OTP-логин).

**Request:**
```json
{
  "text": "string, required — текст отзыва",
  "rating": "int, optional — оценка"
}
```

**Response (200):**
```json
{
  "serviceInfo": {"success": true},
  "id": "string — ID feedback в InfraConnect"
}
```

**Errors:**
- `INVALID_JSON` (400) — невалидный JSON
- `INVALID_REQUEST` (400) — text пуст
- `USER_NOT_INFRACONNECT` (400) — пользователь не привязан к InfraConnect
- `INFRACONNECT_NOT_CONFIGURED` (503) — INFRACONNECT_BASE_URL не задан
- `INFRACONNECT_ERROR` (502) — ошибка InfraConnect
- `DB_ERROR` (500) — ошибка базы данных при чтении пользователя

**Side effects:**
- Запись feedback в InfraConnect (внешний сервис)

---

## GET /v1/admin/feedback

Список feedback от пользователей. Proxy в InfraConnect.

**Query:** `?page=1&size=50` (size max 100)

**Response (200):**
```json
{
  "serviceInfo": {"success": true},
  "items": [
    {
      "id": "string",
      "userId": "string — InfraConnect user ID",
      "text": "string",
      "rating": 5,
      "createdAt": "RFC3339"
    }
  ],
  "total": 42
}
```

**Errors:**
- `INFRACONNECT_NOT_CONFIGURED` (503) — INFRACONNECT_BASE_URL не задан
- `INFRACONNECT_ERROR` (502) — ошибка InfraConnect

---

# Служебные

## GET /health

Проверка доступности сервера. Без аутентификации.

**Response 200:**
```json
{
  "name": "SwanCode API",
  "status": "ok"
}
```

---

## Коды ошибок

| Код | HTTP | Описание |
|-----|------|----------|
| INVALID_JSON | 400 | Невалидный JSON в запросе |
| EMPTY_MESSAGE | 400 | Пустое сообщение |
| PROVIDER_REQUIRED | 400 | Не указан провайдер |
| MODEL_REQUIRED | 400 | Не указана модель |
| EMPTY_TEMPLATE | 400 | Пустой шаблон промпта |
| SESSION_REQUIRED | 400 | Не указан sessionId в retry-запросе |
| FAILURES_REQUIRED | 400 | Пустой массив failures в retry-запросе |
| KEY_REQUIRED | 400 | Не указан key в admin config запросе |
| CONFIG_UPDATE_FAILED | 400 | Валидация значения конфига не прошла |
| PROVIDER_NOT_FOUND | 404 | Провайдер не найден |
| PROMPT_NOT_FOUND | 404 | Промпт не найден |
| PROMPT_EXISTS | 409 | Промпт с таким именем уже существует |
| SYSTEM_PROMPT_PROTECTED | 403 | Системные промпты нельзя удалять |
| TABLE_NOT_FOUND | 404 | Таблица не найдена |
| INVALID_SORT | 400 | Указанная колонка сортировки не существует |
| MISSING_TABLE | 400 | Не указано имя таблицы |
| MISSING_ID | 400 | Не указан ID пользователя |
| USER_NOT_FOUND | 404 | Пользователь не найден |
| USER_DELETED | 403 | Аккаунт пользователя удалён |
| USER_NOT_INFRACONNECT | 400 | Пользователь не привязан к InfraConnect |
| ADMIN_NOT_FOUND | 404 | Админ не найден |
| ADMIN_LOGIN_TAKEN | 409 | Login админа уже занят |
| INFRACONNECT_NOT_CONFIGURED | 503 | INFRACONNECT_BASE_URL не задан |
| KEY_VALIDATION_UNAVAILABLE | 503 | InfraConnect недоступен при валидации per-user ключа (сеть / 5xx / нераспознанный код) — не маскируем под 401/500, клиент ретраит |
| INFRACONNECT_ERROR | 502 | Ошибка InfraConnect |
| USER_CREATE_FAILED | 500 | Ошибка создания пользователя |
| INSUFFICIENT_BALANCE | 402 | Недостаточно средств для запроса |
| BILLING_NOT_CONFIGURED | 503 | Swan_Billing не сконфигурён/недоступен (hard-dependency, F-000012) |
| BILLING_ERROR | 500 | Ошибка биллинга (нераспознанный сбой при резервировании) |
| SESSION_NOT_FOUND | 404 | Сессия не найдена |
| PROVIDER_ERROR | 400 | Неизвестное имя провайдера (не канон и не алиас) |
| SESSION_ERROR | 500 | Ошибка сессии |
| DB_ERROR | 500 | Ошибка базы данных |
| AI_NOT_CONFIGURED | 503 | AIROUTER_BASE_URL/CONSUMER_KEY не заданы — AI-провайдер отключён (v0.33.0) |
| AI_REQUEST_FAILED | 502 | Ошибка запроса к AI (Router/сеть/наш consumer-key; деталь в errorlog) |
| AI_UNAVAILABLE | 503 | Router: модель некому обслужить (NO_PROVIDER_AVAILABLE) или модель не найдена в каталоге (v0.33.0) |
| AI_TIMEOUT | 504 | Router: upstream-провайдер не ответил вовремя (v0.33.0) |
| MODEL_NOT_ALLOWED | 403 | Модель вне allow-list продукта (F-000015) либо отказ Router'а по consumer_access |
| MODEL_DISPLAY_OVERRIDE_FAILED | 500 | Ошибка чтения admin display-name overrides для каталога моделей |
| STREAMING_NOT_SUPPORTED | 501 | /chat/stream отключён в v0.33.0 (Router-SSE — T-000141) |
| MODELS_FETCH_FAILED | 502 | Ошибка получения моделей |
| BALANCE_FETCH_FAILED | 502 | Ошибка получения баланса |
| CONFIG_LOAD_FAILED | 500 | Ошибка чтения runtime-конфига из БД |
| UNAUTHORIZED | 401 | Отсутствует или неверный ключ аутентификации |
| RATE_LIMIT_EXCEEDED | 429 | Превышен лимит запросов |
| UPDATES_NOT_CONFIGURED | 503 | SWANUPDATES_BASE_URL/SERVER_KEY не заданы |
| UPDATES_NOT_AVAILABLE | 404 | У продукта пустой `updates_product_name` (апдейт-канал не настроен) |
| RELEASE_NOT_FOUND | 404 | Нет релизов под (продукт, platform) в SwanUpdates |
| RATE_LIMITED | 429 | Лимит SwanUpdates (Retry-After пробрасывается) |
| UPDATES_UPSTREAM_ERROR | 502 | SwanUpdates недоступен или вернул ошибку |

---

## Системные промпты (promptCode)

Пользовательские промпты:

| Код | Описание |
|-----|----------|
| project_analysis | Анализ проекта |
| error_explain | Объяснение ошибки |
| architecture_analysis | Анализ архитектуры |
| bug_search | Поиск ошибок |
| improvement_recommendations | Рекомендации по улучшению |
| free_form | Свободный формат |

Системные фрагменты (используются сервером, не передаются клиентом):

| Код | Описание |
|-----|----------|
| _system_preamble | Преамбула Senior-разработчика |
| _system_code_change | Поведенческие инструкции для провайдеров с native tool_use |
| _system_code_change_fallback | Инструкции SEARCH/REPLACE формата для провайдеров без tool_use (GigaChat) |

---

## GET /v1/admin/products

Список всех продуктов с агрегатными счётчиками. Default-продукт первым в выдаче. Status: `implemented` (с фазы 1, F-000001).

**Request:** заголовки `X-Admin-Key`. Тело пустое.

**Response (200):**
```json
{
  "serviceInfo": "(standard)",
  "products": [
    {
      "id": "uuid",
      "name": "string — технический ID (используется в toolset.ForProduct switch, миграциях, X-Product-Key)",
      "displayName": "string — человекочитаемое имя для UI (T-000064, v0.21.0). Пустая строка → fallback на name на стороне UI",
      "description": "string",
      "isActive": "bool",
      "compactionEnabled": "bool",
      "compactionThresholdPct": "int 1..100",
      "defaultSummarizerModel": "string",
      "updatesProductName": "string — имя продукта в SwanUpdates (updater); \"\" = апдейт-канал не настроен (F-000016, миграция 032)",
      "isDefault": "bool — true для зарезервированного default-продукта",
      "toolset": "[]{name, description} — read-only снапшот из toolset.ForProduct (T-000058)",
      "createdAt": "RFC3339",
      "updatedAt": "RFC3339",
      "sessionsCount": "int — общее число сессий продукта",
      "keysCount": "int — число НЕревокированных product_keys",
      "canHardDelete": "bool — после T-000058 всегда false (продукты code-defined, hard-delete через admin-API недоступен; поле сохранено для обратной совместимости DTO-формы)"
    }
  ]
}
```

**Поле `toolset`** (T-000058 / v0.20.2) — список tools'ов, доступных AI в этом продукте. Источник — code-defined `toolset.ForProduct(name)` в `internal/domain/toolset/`, не БД. Каждый элемент: `name` (string), `description` (string). Read-only; меняется только релизом сервера.

**Поле `displayName`** (T-000064 / v0.21.0) — отдельное от `name` человекочитаемое имя для UI-меток в админке. `name` остаётся техническим идентификатором и не меняется (используется в URL, toolset switch, X-Product-Key payload). Server-side контракт остаётся product-терминологией; rename «Продукт» → «Клиент» в UI — задача админки (см. ANNOUNCE-002).

## GET /v1/admin/products/{id}

Детали одного продукта (без счётчиков). Status: `implemented`.

**Errors:**
- `MISSING_ID` (400) — id отсутствует в URL
- `PRODUCT_NOT_FOUND` (404)

## POST /v1/admin/products

**Удалено в v0.20.2 (T-000058).** Status: `deprecated` → endpoint возвращает 405.

Продукты code-defined: имя продукта зашито в `toolset.ForProduct` switch + БД-row добавляется миграцией. Создание через админ-API создавало бы phantom-продукт (row в БД есть, но `toolset.ForProduct` для него возвращает default-набор молча), поэтому endpoint отбит.

**Добавление нового продукта:** миграция БД (INSERT в `products`) + регистрация имени в `internal/domain/toolset/toolset.go` (case в `ForProduct` switch). Релиз сервера.

**Response (405):**
```json
{
  "serviceInfo": {"success": false, "errorCode": "PRODUCTS_CODE_DEFINED"},
  "message": "Продукты определены в коде сервера..."
}
```

## PATCH /v1/admin/products/{id}

Partial-update. Status: `implemented`.

**Request:** все поля опциональны (см. POST). Любое не упомянутое в body поле остаётся неизменным.

**Partial update semantics:**
- **Поле absent в body** → значение в БД не меняется
- **Поле передано как `null`** → JSON-парсинг превращает в nil pointer, эквивалентно absent (не меняется)
- **Поле передано как `""`** (пустая строка) → значение в БД перезаписывается на пустую строку (для `description`, `defaultSummarizerModel`, `displayName`, `updatesProductName`)
- **`name` = `""`** → 400 INVALID_NAME (не разрешено очистить)
- **`displayName` поддерживается с v0.21.0 (T-000064)** — partial-update, как у остальных полей
- **`updatesProductName` поддерживается с миграции 032 (F-000016)** — имя продукта в SwanUpdates; `""` очищает (продукт без апдейт-канала → 404 UPDATES_NOT_AVAILABLE)

**Response (200):** `{serviceInfo, product: ProductItem}`.

**Errors:**
- `MISSING_ID` (400)
- `INVALID_JSON` (400)
- `INVALID_NAME` (400) — попытка очистить name
- `INVALID_THRESHOLD` (400)
- `PRODUCT_NOT_FOUND` (404)
- `PRODUCT_NAME_EXISTS` (409) — переименование на занятое имя
- `CANNOT_DELETE_DEFAULT_PRODUCT` (403) — попытка переименовать default или поставить `isActive=false` для default

## DELETE /v1/admin/products/{id}

**Удалено в v0.20.2 (T-000058).** Status: `deprecated` → endpoint возвращает 405.

Продукты code-defined; удаление неподдерживается. Чтобы временно «выключить» продукт — `PATCH /v1/admin/products/{id} {isActive: false}` (мягкая деактивация без удаления row'а, существующие сессии перестают принимать новый трафик).

**Response (405):**
```json
{
  "serviceInfo": {"success": false, "errorCode": "PRODUCTS_CODE_DEFINED"},
  "message": "Продукты определены в коде сервера..."
}
```

## GET /v1/admin/products/{id}/keys

Список product_keys конкретного продукта. Plaintext-secret не возвращается ни в одном ключе. Сортировка: активные сначала, дальше revoked, внутри по `createdAt DESC`. Status: `implemented`.

**Response (200):**
```json
{
  "serviceInfo": "(standard)",
  "keys": [
    {
      "id": "uuid",
      "prefix": "8 hex chars — public-часть ключа",
      "name": "string — операторская метка",
      "lastUsedAt": "RFC3339, optional, omitted в фазе 1 (T-000047)",
      "revokedAt": "RFC3339, optional, omitted если ключ активен",
      "createdAt": "RFC3339"
    }
  ]
}
```

**Errors:**
- `MISSING_ID` (400)
- `PRODUCT_NOT_FOUND` (404)

## POST /v1/admin/products/{id}/keys

Выпускает новый product_key для продукта. **`fullKey` в ответе содержит plaintext `swan_pk_{prefix}_{secret}` — выдаётся ОДИН раз**, в БД хранится только `bcrypt(secret)`. Status: `implemented`.

**Request:** body опционален (можно пустое тело — name становится пустой строкой).
```json
{
  "name": "string, optional, default '' — операторская метка ключа"
}
```

**Response (201):**
```json
{
  "serviceInfo": "(standard)",
  "key": {
    "id": "uuid",
    "prefix": "8 hex chars",
    "name": "string",
    "createdAt": "RFC3339"
  },
  "fullKey": "swan_pk_{prefix(8 hex)}_{secret(32 hex)} — единственный момент когда secret виден"
}
```

**Errors:**
- `MISSING_ID` (400)
- `INVALID_JSON` (400)
- `PRODUCT_NOT_FOUND` (404)
- `PRODUCT_INACTIVE` (403) — продукт soft-deleted (`is_active=FALSE`); сначала ре-активируйте через `PATCH /v1/admin/products/{id} {isActive:true}`
- `KEY_ISSUE_FAILED` (500)

**Side effects:**
- INSERT в `product_keys` с `revoked_at=NULL`, `last_used_at=NULL`

## DELETE /v1/admin/products/{id}/keys/{keyId}

Soft-revocation: `UPDATE product_keys SET revoked_at = NOW() WHERE id = $1`. Идемпотентно (повторный revoke на уже-revoked возвращает 200). Status: `implemented`.

**Response (200):** `{"serviceInfo": {"success": true}}` — стандартный envelope без дополнительных полей.

**Errors:**
- `MISSING_ID` (400) — отсутствует id или keyId
- `PRODUCT_NOT_FOUND` (404) — продукт не найден
- `KEY_NOT_FOUND` (404) — ключ не найден

**Side effects:**
- `UPDATE product_keys SET revoked_at = NOW() WHERE id = $1 AND revoked_at IS NULL` — с этого момента middleware будет отвергать запросы с этим X-App-Key (401)

---

## GET /v1/admin/products/{id}/models

Текущий per-product allow-list моделей в порядке вывода (`position`). Пустой список = ограничения нет (продукту доступны все модели). Управляет тем, какие модели продукт видит в `GET /v1/api/models/{provider}` и может вызвать в `/v1/api/chat*` (F-000015). Status: `implemented`.

**Response (200):**
```json
{
  "serviceInfo": {"success": true},
  "productId": "uuid",
  "modelIds": ["anthropic/claude-sonnet-4.6", "openai/gpt-5"]
}
```
`modelIds` — `[]`, если allow-list пуст (никогда `null`). Порядок = порядок вывода.

**Errors:**
- `MISSING_ID` (400) — не указан id продукта
- `PRODUCT_NOT_FOUND` (404) — продукт не найден (явный precheck)

---

## PUT /v1/admin/products/{id}/models

Полная замена (replace) allow-list продукта. **Порядок массива = порядок вывода** (`position` = индекс элемента). Дубли убираются с сохранением порядка первого вхождения. Пустой массив `[]` (или `null`/отсутствие поля) = снять ограничение (fail-open, все модели). Существование `modelId` в каталоге **не** валидируется (запись может предшествовать появлению модели). Status: `implemented`.

**Request:**
```json
{
  "modelIds": ["string — id модели как в GET /v1/api/models, в желаемом порядке вывода"]
}
```

**Response (200):** тот же формат, что у `GET` — результирующий (дедупнутый, упорядоченный) список.

**Errors:**
- `MISSING_ID` (400) — не указан id продукта
- `INVALID_JSON` (400) — невалидное тело
- `PRODUCT_NOT_FOUND` (404) — продукт не найден (явный precheck; FK сам по себе 404 не даёт)

**Side effects:**
- Транзакционный replace: `SELECT ... FOR UPDATE` на products-строке (сериализует конкурентные PUT) → `DELETE` allow-list продукта → batch `INSERT` с `position` = индекс. Любая ошибка откатывает (прежний список цел).
- Немедленно влияет на enforcement (`/v1/api/chat*` → `MODEL_NOT_ALLOWED`) и read-filter (`GET /v1/api/models/{provider}`) — общий живой предикат, без кеша.

---

## Поддержка инструментов

С v0.33.0 tool_use идёт единым путём через Swan_AiRouter (wire-формат
`tools=[{name, description, input_schema}]` / `tool_calls=[{id, name, input}]`);
per-provider адаптация — на стороне Router'а (для GigaChat Router сам стрипает
tool-роли — такие модели отвечают plain-текстом без tool-цикла). Текстовый
SEARCH/REPLACE-fallback удалён вместе с direct-GigaChat.

Клиент не видит разницы — ответы приходят в едином формате `codeChanges` /
`executeCommands` / `toolUses` (base64).
