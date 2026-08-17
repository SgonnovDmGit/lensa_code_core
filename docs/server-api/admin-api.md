# Admin API — LenSA Code Server

Server version: 0.86.0

Base URL: `http://localhost:8080`. Все эндпоинты требуют `Authorization: Bearer swan_ak_*`
(admin-ключ). Клиентская поверхность — [client-api.md](client-api.md).

## Топики контракта

| Файл | Содержимое |
|---|---|
| [admin-api-core.md](admin-api-core.md) | server-info, stats, recommendations, prompt reads |
| [admin-api-reports.md](admin-api-reports.md) | отчётные агрегаты usage/revenue: топы, daily-бакеты, product spend |
| [admin-api-users.md](admin-api-users.md) | users CRUD, sessions/messages/versions, tables, prompt writes |
| [admin-api-skills-config.md](admin-api-skills-config.md) | skills CRUD, runtime config |
| [admin-api-microservices.md](admin-api-microservices.md) | microservices health, swanupdates, querycrypt |
| [admin-api-pricing.md](admin-api-pricing.md) | model-prices, display-overrides, database reset |
| [admin-api-admins.md](admin-api-admins.md) | admins CRUD, admin/user keys |
| [admin-api-audit.md](admin-api-audit.md) | admin-actions, user-auth-events, error-logs, feedback |
| [admin-api-generations.md](admin-api-generations.md) | аудит генераций ERF/EPF: список, деталь с телом, retention |
| [admin-api-products.md](admin-api-products.md) | products CRUD, product keys, product models |

## Access model

Общая модель доступа и заголовки — те же, что в client-контракте (продублировано для
самодостаточности при синке):

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
  - `lens_ak_{prefix}_{secret}` — admin-ключ (новый выпуск; легаси `swan_ak_*` принимается)
  - `swan_uk_{prefix}_{secret}` — user-ключ, **легаси** (серверо-локальный OTP/admin-канал — дремлет; выданные ключи принимаются)
  - `{prefix}_{live|test}_{39 base62}` — **InfraConnect per-user API-ключ** (F-000029, v0.47.0): персональный ключ, выпущенный платформой InfraConnect (напр. `lens_live_…`). Сервер валидирует его S2S-раундтрипом к InfraConnect (`POST /v1/auth/api-keys/validate`, кэш 5 мин) и резолвит в локального пользователя по `infraconnect_id`. Принимается везде, где принимается `swan_uk_*`. **Это боевой путь авторизации пользователей** (CLI / консоль).
- **`X-Product-Key: <key>`** — identity **клиентского приложения**:
  - `lens_pk_{prefix}_{secret}` — product-ключ (зашит в сборку клиента; новый выпуск — `lens_pk_`, легаси `swan_pk_*` принимается)

**Шапки ключей (deep-rename T-000330, v0.83.0):** новые pk/ak-ключи выпускаются с брендовой шапкой `lens_`; ранее выданные `swan_*`-ключи принимаются **вечно** (grandfather — перевыпуск не требуется, длина ключа не изменилась). Далее по документу обозначения `swan_uk_*` / `swan_pk_*` / `swan_ak_*` читать как **популяции** ключей — обе шапки эквивалентны.

Product-ключ (`lens_pk_*`/`swan_pk_*`) в `Authorization` **не принимается** — для product-идентификации используется только `X-Product-Key`. Это разделение нужно для chat-эндпоинтов, где требуется одновременно знать **кто** обращается (user) и **из какого приложения** (product) — для биллинга и аналитики соответственно.

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

- **Issued `lens_ak_<8hex>_<32hex>`** (легаси `swan_ak_*` принимается) — стандартный admin key, выпускается через `POST /v1/admin/admins/me/keys` или `POST /v1/admin/admins/{id}/keys`. Plaintext возвращается **один раз** в response, после — bcrypt-only в БД.
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
| `GET /v1/api/models`, `/balance`, `/recommendations/{language}` (+ deprecated `/providers`, `/models/{provider}`, `/balance/{provider}`) | `swan_ak_*` (опц.) | `swan_pk_*` (опц.) — нужен **один** из двух |
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

**Понятие провайдера убрано из клиентского контракта (T-000329, v0.83.0)** — сервер единый провайдер. Канон — provider-less пути: `GET /v1/api/models` и `GET /v1/api/balance`. Формы `/{provider}` и `GET /v1/api/providers` — deprecated-толерантные: значение `provider` в запросах и path-параметрах принимается, но **игнорируется** (так с v0.40.0/T-000148; legacy-имена `OpenRouter`/`Claude`/`ChatGPT`/`DeepSeek`/`GigaChat` перестали резолвиться тогда же). Атрибуционная строка провайдера в ответах/истории — `LenSA` (новые записи; старые несут `SwanCode` и прежние имена — это display-строки, матчить их нельзя).

Биллинг пользователей считается по **внутреннему прайсу** сервера (таблица `model_prices`, коины; админ-управление через `/v1/admin/model-prices`). Закупочные цены Router'а в клиентские ответы не попадают.

Конфигурация: env `AIROUTER_BASE_URL` + `AIROUTER_CONSUMER_KEY` (см. `.env.example`). Если не заданы — сервер живёт в деградации: chat/models/balance отвечают `503 AI_NOT_CONFIGURED`.

Ошибки Router'а мапятся в коды сервера: `MODEL_NOT_ALLOWED` (403), `AI_UNAVAILABLE` (503), `AI_TIMEOUT` (504), прочее → `AI_REQUEST_FAILED` (502); детали — только в errorlog.


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

