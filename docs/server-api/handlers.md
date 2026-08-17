# Handlers Map — swancode_server

Маппинг URL → middleware-цепочка → handler. Версия: **0.42.0**.

Source of truth — [internal/transport/http/router.go](../internal/transport/http/router.go). Этот файл отражает текущее состояние router'а. После любой правки router.go — синхронизировать.

Контракт (request body / response body / errors / side effects) — в [docs/my_api/](my_api/) (`client-api.md` / `admin-api.md` + топики). Этот файл — только router-map (что куда роутится, какими guard'ами защищено, в каком handler-файле живёт).

## Middleware

### Глобальная цепочка (снаружи внутрь)

```
CORS → Recovery → ClientIP → UserAgent → RequestID → Logger → router (mux)
```

- **CORS** — `Access-Control-Allow-Origin: *`, preflight `OPTIONS → 204`
- **Recovery** — перехват паник, лог в `error_logs` + `500`
- **ClientIP** — `X-Forwarded-For → X-Real-IP → RemoteAddr` в context (для аудита)
- **UserAgent** — `User-Agent` заголовок в context (для аудит-эмиттеров T-000047)
- **RequestID** — `X-Request-ID` (passthrough или UUID-генерация)
- **Logger** — `slog`: method, path, status, duration, IP, request_id

### Per-route цепочка

Двухключевая auth (Phase 5a + T-000047 audit):

```
authMW         → разбирает Authorization: Bearer swan_{ak,uk}_*
                 не блокирующий: пустой заголовок проходит насквозь
                 эмитит auth.success / auth.fail в audit log

pkMW           → разбирает X-Product-Key: swan_pk_*
                 не блокирующий: пустой заголовок проходит насквозь
                 эмитит auth.success / auth.fail в audit log

requireAdmin            → 401 если admin identity отсутствует в context
requireUser             → 401 если user identity отсутствует
requireProduct          → 403 PRODUCT_REQUIRED если product identity отсутствует
requireAdminOrProduct   → 401 если ни admin, ни product
requireUser+requireProduct → оба требуются (chat, /v1/api/me)

rateMW         → in-memory per-user (fallback IP, T-000119):
                 chat — 60/5мин (initiation: /chat, /chat/stream) и 600/5мин
                 (tool-loop: /chat/tool-results, /chat/tool-retry);
                 updates — 30/5мин общий bucket на оба роута (F-000016)

attestationMW  → пломба X-QC-Token, гейт ТОЛЬКО на query_console_1c (F-000017/028):
                 вокруг chatMux; trial-decrypt по версиям ключа (current/retired-grace);
                 observe — log+audit+пропуск; enforce — 403 (нет токена) /
                 426 COMPONENT_OUTDATED (retired) / 401 (невалид).
                 Прозрачен для прочих продуктов и неактивной подсистемы.
```

## Префиксы и Auth-требования

| Префикс | Auth | Назначение |
|---------|------|------------|
| `/health` | — | Health check |
| `/v1/auth/otp/*` | `X-Product-Key` | OTP send/verify через InfraConnect |
| `/v1/api/me*` | `swan_uk_*` + `X-Product-Key` | Пользовательский профиль, баланс, история использования |
| `/v1/api/chat*` | `swan_uk_*` + `X-Product-Key` (+ rate limit) | Chat completion, tool-results, tool-retry, stream |
| `/v1/api/providers`, `/v1/api/models/*`, `/v1/api/balance/*`, `/v1/api/recommendations/*` | (`swan_ak_*` или `swan_pk_*`) — `requireAdminOrProduct` | Read-only по provider/model данным |
| `/v1/feedback` | `swan_uk_*` + `X-Product-Key` | Отправка отзыва (proxy в InfraConnect) |
| `/v1/api/updates/*` | `swan_uk_*` + `X-Product-Key` (+ rate limit 30/5мин общий) | Проверка/скачивание обновлений клиента (proxy в SwanUpdates, F-000016) |
| `/v1/admin/*` | `swan_ak_*` | Admin-операции (все, без исключений) |

## Маршруты

### Public

| Метод | Путь | Handler | Файл |
|-------|------|---------|------|
| GET | `/health` | `HealthCheck` | `handlers/health.go` |

### OTP (`X-Product-Key`, требует `infraconnect.base_url`)

| Метод | Путь | Handler | Файл |
|-------|------|---------|------|
| POST | `/v1/auth/otp/send` | `OTPHandler.Send` | `handlers/otp.go` |
| POST | `/v1/auth/otp/verify` | `OTPHandler.Verify` | `handlers/otp.go` |

Эмитит `user_auth_events`: `otp.sent` (Send), `otp.verified` + `key.issued` (Verify). REQ-020 wire: при непустом `name` в InfraConnect-ответе вызывает `UpdateDisplayName` для кеша имени.

**Конфиг InfraConnect (config-in-DB, v0.38.0):** `base_url` + `product_key` — из `server_config` (env `INFRACONNECT_*` = seed); `PUT /v1/admin/config` на `infraconnect.*` пересобирает клиент и атомарно подменяет через **общий `infraconnect.Holder`**, разделяемый OTP-send/verify + Feedback (create/admin-list), без рестарта. Каждый запрос снимает снапшот `Get()` в начале; OTP send→verify терпит подмену между шагами (OTP-код живёт в InfraConnect, не у нас). Пустой `base_url` (БД + env) → 503 `INFRACONNECT_NOT_CONFIGURED`.

### User-facing (`swan_uk_*` + `X-Product-Key`)

| Метод | Путь | Handler | Файл |
|-------|------|---------|------|
| GET | `/v1/api/me` | `UsersHandler.Me` | `handlers/users.go` |
| GET | `/v1/api/me/usage` | `UsersHandler.Usage` | `handlers/users.go` |
| GET | `/v1/api/sessions` | `ProductSessionsHandler.List` | `handlers/product_sessions.go` |
| POST | `/v1/api/sessions/batch` | `ProductSessionsHandler.Batch` | `handlers/product_sessions.go` |
| DELETE | `/v1/api/sessions/{id}` | `ProductSessionsHandler.Delete` | `handlers/product_sessions.go` |
| GET | `/v1/api/sessions/{id}/messages` | `ProductSessionsHandler.Messages` | `handlers/product_sessions.go` |
| PATCH | `/v1/api/sessions/{id}` | `ProductSessionsHandler.Rename` | `handlers/product_sessions.go` |
| GET | `/v1/api/sessions/{id}/query-versions` | `ProductSessionsHandler.QueryVersions` | `handlers/product_sessions.go` |
| GET | `/v1/api/sessions/{id}/query-versions/{versionId}` | `ProductSessionsHandler.QueryVersionByID` | `handlers/product_sessions.go` |
| POST | `/v1/feedback` | `FeedbackHandler.Create` | `handlers/feedback.go` |

### Updates (`swan_uk_*` + `X-Product-Key` + общий rate limit 30/5min, F-000016)

| Метод | Путь | Handler | Файл |
|-------|------|---------|------|
| GET | `/v1/api/updates/check` | `UpdatesHandler.Check` | `handlers/updates.go` |
| GET | `/v1/api/updates/download` | `UpdatesHandler.Download` | `handlers/updates.go` |

Гейт конструирования: `UpdatesClient != nil && ProductRepo != nil` (handler собирается **заранее** — общий инстанс для user-facing роутов и admin-config hot-reload, v0.38.0; роуты вложены в user-facing секцию под `UsersRepo != nil`). Конфиг (base_url/server_key) — из `server_config` (env `SWANUPDATES_*` = разовый seed); `PUT /v1/admin/config` на `swanupdates.*` валидирует → персистит → атомарно (`atomic.Pointer[updatesConfig]`) подменяет клиента без рестарта — активный download-стрим доживает со своим снимком (новые запросы видят новый клиент). Имя продукта в SwanUpdates — атрибут продукта (`products.updates_product_name`, миграция 032), резолвится per-запрос по `X-Product-Key`; пусто → `404 UPDATES_NOT_AVAILABLE`. Без конфига клиент `IsConfigured()==false` → роуты живут, отвечают 503. Download дополнительно ограничен семафором конкурентности (cap 8 стримов глобально, сверх → 429; семафор НЕ подменяется при reload — свойство сервера, не конфига) и стримит со скользящим write-deadline (60s/чанк) вместо глобального WriteTimeout; чтение из upstream под progress-watchdog'ом (60s без прогресса → обрыв, слот семафора освобождается).

**v0.39.0 — bootstrap server_key (F-000025, REQ-030).** `POST /v1/admin/swanupdates/bootstrap` (`AdminConfigHandler.BootstrapSwanUpdates`, [admin_config.go](../internal/transport/http/handlers/admin_config.go)) провижнит `server_key` по admin-ключу SwanUpdates: `swanupdates.ProvisionServerKey` ([swanupdates/admin.go](../internal/infrastructure/swanupdates/admin.go)) ходит `X-Admin-Key` в `GET /v1/admin/servers` → ротирует найденный `swancode_server` (`regenerate-key`) либо создаёт (`POST /v1/admin/servers`); fetch'а существующего ключа у SwanUpdates нет (одноразовый). Цепочка side-effect'ов: одноразовый `swan_srv_` → `Set(swanupdates.server_key)` → `ReloadUpdates` (hot-reload, тот же atomic-холдер `updatesConfig`, что и `PUT /config`) → опц. `Set(swanupdates.admin_key)`. Битый admin-ключ → мягкая `ok:false SWANUPDATES_AUTH_FAILED` (HTTP 200, отличить от сети), сеть/таймаут → `502 SWANUPDATES_UPSTREAM_ERROR`. Пустое тело = re-bootstrap из сохранённого `swanupdates.admin_key`. Гейт `updates==nil` → `503` (как у `updateSwanUpdates`).

**v0.52.0 — admin test-download (T-000186 / REQ-032).** `POST /v1/admin/swanupdates/test-download` (`UpdatesHandler.TestDownload`, [updates.go](../internal/transport/http/handlers/updates.go)) делает `check` + bounded `download` с `Range` и возвращает метаданные прочитанного sample. Продукт берётся из `product`, `productId -> updates_product_name` или единственного активного продукта с заполненным `updatesProductName`.

**v0.52.1 — download cache/singleflight/retry (T-000197 / ANNOUNCE-002).** User-facing `GET /v1/api/updates/download` подключает локальный artifact cache из `swanupdates.cache_*`: full download без `Range` берёт/публикует файл через singleflight, проверяет checksum до публикации, retry'ит transport/429/5xx с backoff и при `404 RELEASE_NOT_FOUND` один раз повторяет `check` для свежего `releaseId`. `Range`/`If-Range` на cache-hit обслуживается из локального файла через `ServeContent`; cold Range остаётся прямым passthrough в SwanUpdates. `POST /v1/admin/swanupdates/test-download` намеренно остаётся прямой upstream-диагностикой без cache.

### Chat (`swan_uk_*` + `X-Product-Key` + rate limit 60/5min)

| Метод | Путь | Handler | Файл |
|-------|------|---------|------|
| POST | `/v1/api/chat` | `ChatHandler.Chat` | `handlers/chat.go` |
| POST | `/v1/api/chat/stream` | `ChatHandler.ChatStream` — заглушка `501` (v0.33.0, Router-SSE — T-000141) | `handlers/stream.go` |
| POST | `/v1/api/chat/tool-retry` | `RetryHandler.Retry` | `handlers/retry.go` |
| POST | `/v1/api/chat/tool-results` | `RetryHandler.ToolResults` | `handlers/retry.go` |

**v0.33.0 — error-маппинг Router'а.** Все три AI-пути в err-ветке после `prov.ChatCompletion` зовут `respondAIError` ([ai_errors.go](../internal/transport/http/handlers/ai_errors.go)) **строго после** `ReleaseReservation`: типизированная `provider.RouterError` → 403/503/504/502, `Internal`-деталь только в errorlog. Резолв провайдера во всех путях распознаёт `provider.ErrNotConfigured` → `503 AI_NOT_CONFIGURED`. `SaveUsage` пишет канонический `provider_name='SwanCode'`.

Mounted через `http.StripPrefix("/v1/api", chatMux)` — внутренние пути в submux начинаются с `/chat`. Cycle на tool_use'ах: server отдаёт `toolUses[]`, клиент шлёт результаты через `tool-results`, цикл повторяется до финального `apply_query_change` (или `apply_code_change` для swan_code_client).

**v0.25.1 — buffer-семантика для query_console_1c.** Apply-тулы редактирования (`replace_table`, `insert_table`, `delete_table`, `replace_query_fragment`) теперь модифицируют внутренний буфер на 1С-стороне, в редактор не пишут. Финал — `apply_query_change()` без аргумента (коммит буфера) либо с `queryText` (direct write). Buffer живёт на клиенте; сервер — проходная точка (контракт + unpacker'ы в `internal/domain/tools/console1c/`).

**T-000227 — `replace_query_fragment` server-resolved.** Исключение из строки выше: 1С дифф-логику для фрагментных правок не реализует, поэтому дифф считает СЕРВЕР (`resolveToolUses` → `ApplyEdits` против канон-головы C3) и отдаёт клиенту готовый `{fullText}`, который тот кладёт в буфер целиком (whole-query). Дифф-ошибки резолвятся инлайн (`REPLACE_FAILED`, server-only итерация, без round-trip). Прочие три edit-тула остаются полностью client-side. Промпт держит тул `[пока недоступен]` до отгрузки буфера в Lensa_Query (ANNOUNCE-002).

**v0.25.1 — buildSystemPrompt per-product dispatch.** `chat.buildSystemPrompt` диспатчит step 5 (tool-instructions fragment) через `toolset.ToolsInstructionsFragmentName(productName)`: `query_console_1c` → `_system_query_style` (стилевая памятка), default → `_system_code_change`. Steps 3-4 (project context + projectText) обёрнуты в `toolset.InjectsProjectContext(productName)` guard — для query_console_1c skip'аются.

**v0.25.1 — heal висящего `apply_query_change`.** `Chat` / `ChatStream` в начале нового хода (после загрузки сессии, **до** `AddUserText`) вызывают `backfillDanglingApply` ([dangling_apply.go](../internal/transport/http/handlers/dangling_apply.go)). Если последнее сообщение сессии — `KindAssistantTool` с `apply_query_change` без парного `tool_result` (1С на success не шлёт `/chat/tool-results`), сервер дописывает синтетический success-`tool_result` (`APPLY_OK: запрос применён в редактор.`). Иначе OpenAI-совместимые провайдеры (Azure) отвергают историю с `tool_use` без пары (`400 «No tool output found»`). Persist last-only: уже-отравленные сессии (apply в середине истории) не восстанавливаются.

**v0.26.0 — prompt-storage reconcile + console_1c surface 16.** Канон дефолт-промптов `query_console_1c` (3 строки) вынесен из миграций в `prompts_default/query_console_1c/*.md`; `repository.ReconcilePromptDefaults` вшивает их в `prompts.default_template` (и `template`, если админ не правил) на старте — см. [docs/flows/prompt-storage.md](flows/prompt-storage.md). `console1c.All()` расширен 12 → 16 тулов (+`get_query_metrics` / `get_table_text` / `get_table_field_count` / `get_console_settings`); `validate_query` без параметра; `delete_table` мульти (`tableIndexes[]`); `insert_table` unpacker fix `insertedAt`→`newTableIndex`; `get_query_tables` +`executionTimeMs?`/`rowCount?`.

**v0.29.0 — per-product model allow-list (F-000015).** Все 4 write-пути (`Chat`/`ChatStream`/`ToolResults`/`Retry`) вызывают `ChatHandler.checkModelAllowed` ([model_access.go](../internal/transport/http/handlers/model_access.go)) **сразу после резолва сессии, ДО** любых side-effect'ов (`backfillDanglingApply`/`recorder.Record`/`AddUserText`/`AddToolResult`/`SaveMessage`/`ReserveBalance`/AI-вызова). Гейт читает живой `restricted`-предикат из `product_allowed_models` по `sess.ProductID` + `sess.Model`: пустой allow-list → пропуск (fail-open); модель не в списке → `403 MODEL_NOT_ALLOWED`; **сбой БД → `500 MODEL_ACCESS_CHECK_FAILED` (fail-CLOSED, не pass-through)** — access-gate не пропускает при сбое. Read-filter (`ListModels`) и enforcement читают один живой предикат, без cross-request кеша в v0.29.0.

**2026-06 — толерантность к array/composite `type` + server-managed порог.** Поле `type` в результатах `get_object_metadata` / `get_query_parameters` парсится через `attrType` ([attr_type.go](../internal/domain/tools/console1c/attr_type.go)) — принимает строку / массив строк / `{composite,typeCount}` / null / мусор и **никогда не возвращает ошибку**: поле `type` больше не роняет тул в `INVALID_TOOL_DATA`. Рендер: строка как есть, массив через `", "`, composite → `составной тип (N вариантов)`. Порог схлопывания сервер задаёт настройкой `server_config` `console1c.composite_type_threshold` (дефолт 5, hot-reload) и домёрдживает в `input` обоих тулов (`compositeTypeThreshold`) в `splitToolUses` ([chat.go](../internal/transport/http/handlers/chat.go)) — единый chokepoint для chat + обоих retry-путей; не-объектный input пропускается, прочие тулы не трогаются. Раскрытие составного типа по требованию (`get_attribute_type_details`) — следующим релизом.

**v0.42.0 — attestation-пломба на `query_console_1c` (F-000017).** В chat-цепочку после `requireProduct`/`requireUser`, **вокруг** `chatMux`, вшит `attestationMiddleware` ([middleware_attestation.go](../internal/transport/http/middleware_attestation.go)). Гейт применяется только когда `product_id` запроса == UUID `query_console_1c` (резолв по имени на старте) — прочие продукты проходят прозрачно. Middleware читает и **восстанавливает** тело (хендлеры читают его дальше; `bodyHash` — по сырым байтам), распечатывает `X-QC-Token` снапшот-cipher'ом `Holder` ([infrastructure/attestation](../internal/infrastructure/attestation/)) и валидирует пломбу ([domain/attestation](../internal/domain/attestation/): user/body/свежесть/sig/анти-реплей). Исход → `user_auth_events` (event `attestation`). Анти-реплей-кэш + валидатор — синглтоны (не пересоздаются при hot-reload ключа). См. [docs/flows/querycrypt-attestation.md](flows/querycrypt-attestation.md).

**v0.43.0 — ротация-с-grace ключа пломбы (F-000028).** `Snapshot` держит **набор** живых версий ключа (current + retired в grace-окне `querycrypt.key_grace_days`); `validateAttestation` **trial-decrypt'ит** токен по версиям (current-первой) — первая расшифровавшая (AES-GCM аутентифицирует) считается «той самой», валидируется её рецепт-секретом. Полностью валидная пломба на **current** → пропуск; на **retired** → `426 COMPONENT_OUTDATED` (enforce; устаревшая компонента, апдейт тянется штатно — update-путь пломбой не гейтится); подделка / ключ вне grace → `401 ATTESTATION_INVALID`; нет токена → `403 ATTESTATION_REQUIRED`. Ключ живёт в таблице `querycrypt_keys` (не в `server_config`), рождается ротацией `POST /v1/admin/querycrypt/rotate` (one-time-reveal seed/mask), env-сид снят. `rebuildAttestationSnapshot` ([admin_config.go](../internal/transport/http/handlers/admin_config.go)) — общая точка пересборки снапшота для hot-reload (`attestation.enforce`/`key_grace_days`) и ротации. Исход добавляет `outcome=outdated` в audit.

**v0.44.0 — console_1c: find_account + единый флаг свёртки `{t:1,c:N}` + ключи cd/s.** Новый тул `find_account` ([unpacker_find_account.go](../internal/domain/tools/console1c/unpacker_find_account.go), tool-set 17 → 18) — точечный поиск счёта в плане счетов (сотни предопределённых). Переиспользуемый generic `collapsible[T]` ([collapsible.go](../internal/domain/tools/console1c/collapsible.go)) парсит коллекцию либо массивом, либо свёрнутым флагом `{t:1,c:N}` (толерантен: неизвестная форма → пусто, без ошибки) — применён к `predefined`, `subkontoTypes.items`; `attrType` получил ветку `{t:1,c:N}` (back-compat `{composite,typeCount}`). Свёрнутую коллекцию рендер разворачивает в инструкцию с тулом точечного раскрытия (`find_account` / `get_object_metadata` по ПВХ / футер `get_attribute_type_details`). `predefinedItem` читает код из `cd` ИЛИ `code` (back-compat); рендер счёта `- cd — s (n)` (наименование + имя предопределённого оба, дедуп при буквальном `s==n`). Промпт console_1c (текст системного промпта — зона консоли) синхронизируется отдельно; разворот флагов и срез ключей — серверный рендер, промпт их не дублирует.

**v0.41.0 — биллинг вынесен в Swan_Billing (F-000012).** Все 4 write-пути (`Chat`/`ChatStream`/`ToolResults`/`Retry`) ходят за деньгами через доменный порт `billing.Service`, реализованный HTTP-фасадом `infrastructure/billing.HTTPService` (S2S-клиент к Swan_Billing + outbox + резолвер `internalUserID→billingUserId` = `infraconnect_id ?? users.id`). **Сигнатуры хендлеров не изменились** — `ReserveBalance`/`CommitReservation`/`ReleaseReservation`/`CurrentBalance` те же, сменилась реализация (pgx → HTTP). `reserve` синхронный (hard-dependency: Billing недоступен → `503 BILLING_NOT_CONFIGURED`, AI не вызывается); `settle`/`release` — durable-outbox (таблица `billing_outbox`, миграция 028): INSERT намерения → инлайн-попытка → успех `done`, неуспех остаётся воркеру-тикеру (`billing.OutboxWorker`, идемпотентные ретраи по `reservationId`). Commit-fail НЕ релизит (settle pending → воркер дотянет) — семантика `committed`-гейтинга `billingSnapshot` сохранена. Зависшие резервы освобождает TTL-backstop Billing (15м); локальный orphan-sweeper снят. Локальные `users.balance_micro`/`balance_reserved_micro` + `balance_reservations` дропнуты (миграция 029); `/me` и admin user-баланс — read-proxy из Billing; `PATCH /admin/users/{id}/balance` снят. См. [docs/flows/auth-billing.md](flows/auth-billing.md).

### Provider/model read (admin или product)

| Метод | Путь | Handler | Файл |
|-------|------|---------|------|
| GET | `/v1/api/providers` | `ProvidersHandler.ListProviders` | `handlers/providers.go` |
| GET | `/v1/api/models/{provider}` | `ProvidersHandler.ListModels` | `handlers/providers.go` |
| GET | `/v1/api/balance/{provider}` | `ProvidersHandler.GetBalance` | `handlers/providers.go` |
| GET | `/v1/api/recommendations/{language}` | `RecommendationsHandler.GetByLanguage` | `handlers/recommendations.go` |

**v0.33.0:** `GetBalance` — агрегированный **закупочный** остаток консумера от Router (`GET /v1/balance`, +`asOf`); `{provider}` в пути — канон `SwanCode` или любой legacy-алиас, ответ одинаков. `ListProviders` отдаёт только канон (`["SwanCode"]` — алиасы структурно скрыты в Registry). Все три точки при пустом AIROUTER_* → `503 AI_NOT_CONFIGURED` (sentinel `provider.ErrNotConfigured`).

**v0.29.0 — read-filter `ListModels` (F-000015).** `ProvidersHandler.applyProductModelFilter` сужает каталог до allow-list продукта запроса и сортирует по `position`. **Admin-identity (`swan_ak_*`) в контексте → полный каталог без фильтра** (для админ-UI чекбоксов; даже если запрос несёт ещё и `X-Product-Key` — `requireAdminOrProduct` пропускает оба). product-only + непустой allow-list → только разрешённые модели в admin-порядке (ключ пересечения — `ModelInfo.ID`). Пустой allow-list → все (fail-open). Сбой загрузки → `500 MODEL_ACCESS_CHECK_FAILED` (fail-closed). `ListProviders` (список имён) **не** фильтруется — имена провайдеров не несут per-model информации.

### Admin: общие (Phase 5a + legacy)

| Метод | Путь | Handler | Файл |
|-------|------|---------|------|
| DELETE | `/v1/admin/database/reset` | `AdminHandler.ResetDatabase` | `handlers/admin.go` |
| GET | `/v1/admin/server-info` | `ServerInfoHandler.Info` | `handlers/server_info.go` |
| GET | `/v1/admin/stats` | `AdminStatsHandler.Stats` | `handlers/admin_stats.go` |

### Admin: prompts

| Метод | Путь | Handler | Файл |
|-------|------|---------|------|
| GET | `/v1/admin/prompts` | `PromptsHandler.List` | `handlers/admin.go` (через `NewPromptsHandler`) |
| GET | `/v1/admin/prompts/{name}` | `PromptsHandler.Get` | `handlers/admin.go` |
| PUT | `/v1/admin/prompts/{name}` | `PromptsHandler.Update` | `handlers/admin.go` |
| POST | `/v1/admin/prompts` | `PromptsHandler.Create` | `handlers/admin.go` |
| DELETE | `/v1/admin/prompts/{name}` | `PromptsHandler.Delete` | `handlers/admin.go` |
| PATCH | `/v1/admin/prompts/{name}/toggle` | `PromptsHandler.Toggle` | `handlers/admin.go` |

**v0.26.0 — reconcile vs admin.** `default_template` для console-промптов `query_console_1c` источается из файлов `prompts_default/` на старте (`ReconcilePromptDefaults`). `PUT` правит только `template`; reconcile перезапишет `template` из файла **только** если он ещё равен `default_template` (админ не правил). `reset` копирует `default_template`→`template` как прежде. См. [docs/flows/prompt-storage.md](flows/prompt-storage.md).

### Admin: tables (debug)

| Метод | Путь | Handler | Файл |
|-------|------|---------|------|
| GET | `/v1/admin/tables` | `AdminTablesHandler.ListTables` | `handlers/admin_tables.go` |
| GET | `/v1/admin/tables/{name}` | `AdminTablesHandler.GetTableContents` | `handlers/admin_tables.go` |

### Admin: sessions

| Метод | Путь | Handler | Файл |
|-------|------|---------|------|
| GET | `/v1/admin/sessions` | `AdminSessionsHandler.List` | `handlers/admin_sessions.go` |
| GET | `/v1/admin/sessions/{id}/messages` | `AdminSessionsHandler.Messages` | `handlers/admin_sessions.go` |
| GET | `/v1/admin/users/{id}/sessions` | `AdminSessionsHandler.UserSessions` | `handlers/admin_sessions.go` |
| GET | `/v1/admin/sessions/{id}/query-versions` | `AdminSessionsHandler.QueryVersions` | `handlers/admin_sessions.go` |

### Admin: recommendations

| Метод | Путь | Handler | Файл |
|-------|------|---------|------|
| GET | `/v1/admin/recommendations` | `AdminRecommendationsHandler.List` | `handlers/admin_recommendations.go` |
| POST | `/v1/admin/recommendations` | `AdminRecommendationsHandler.Create` | `handlers/admin_recommendations.go` |
| PUT | `/v1/admin/recommendations/{id}` | `AdminRecommendationsHandler.Update` | `handlers/admin_recommendations.go` |
| DELETE | `/v1/admin/recommendations/{id}` | `AdminRecommendationsHandler.Delete` | `handlers/admin_recommendations.go` |

### Admin: users (Phase 5a + REQ-019/020 v0.20.0)

| Метод | Путь | Handler | Файл |
|-------|------|---------|------|
| GET | `/v1/admin/users` | `AdminUsersHandler.List` | `handlers/admin_users.go` |
| POST | `/v1/admin/users` | `AdminUsersHandler.Create` | `handlers/admin_users.go` |
| GET | `/v1/admin/users/{id}` | `AdminUsersHandler.Get` | `handlers/admin_users.go` |
| DELETE | `/v1/admin/users/{id}` | `AdminUsersHandler.Delete` | `handlers/admin_users.go` |
| POST | `/v1/admin/users/{id}/keys` | `AdminUsersHandler.IssueUserKey` | `handlers/admin_users.go` |
| GET | `/v1/admin/users/{id}/keys` | `AdminUsersHandler.ListUserKeys` | `handlers/admin_users.go` (REQ-019, v0.20.0) |
| DELETE | `/v1/admin/user-keys/{id}` | `AdminUsersHandler.RevokeUserKey` | `handlers/admin_users.go` |

Все user-objects содержат поле `displayName` (REQ-020, v0.20.0) — кеш имени из InfraConnect, пустая строка пока IC не доставит `name` в OTPVerifyResponse.

### Admin: admins (Phase 5a)

| Метод | Путь | Handler | Файл |
|-------|------|---------|------|
| GET | `/v1/admin/admins` | `AdminsHandler.List` | `handlers/admins.go` |
| POST | `/v1/admin/admins` | `AdminsHandler.Create` | `handlers/admins.go` |
| GET | `/v1/admin/admins/me/keys` | `AdminsHandler.ListOwnKeys` | `handlers/admins.go` |
| POST | `/v1/admin/admins/me/keys` | `AdminsHandler.IssueOwnKey` | `handlers/admins.go` |
| POST | `/v1/admin/admins/{id}/keys` | `AdminsHandler.IssueKey` | `handlers/admins.go` |
| DELETE | `/v1/admin/admin-keys/{id}` | `AdminsHandler.RevokeKey` | `handlers/admins.go` |

### Admin: feedback (proxy в InfraConnect)

| Метод | Путь | Handler | Файл |
|-------|------|---------|------|
| GET | `/v1/admin/feedback` | `FeedbackHandler.AdminList` | `handlers/feedback.go` |

### Admin: products (F-000001 Phase 1)

| Метод | Путь | Handler | Файл |
|-------|------|---------|------|
| GET | `/v1/admin/products` | `AdminProductsHandler.List` | `handlers/admin_products.go` |
| GET | `/v1/admin/products/{id}` | `AdminProductsHandler.Get` | `handlers/admin_products.go` |
| POST | `/v1/admin/products` | `AdminProductsHandler.Create` | `handlers/admin_products.go` |
| PATCH | `/v1/admin/products/{id}` | `AdminProductsHandler.Update` | `handlers/admin_products.go` |
| DELETE | `/v1/admin/products/{id}` | `AdminProductsHandler.Delete` | `handlers/admin_products.go` |
| GET | `/v1/admin/products/{id}/keys` | `AdminProductKeysHandler.List` | `handlers/admin_product_keys.go` |
| POST | `/v1/admin/products/{id}/keys` | `AdminProductKeysHandler.Issue` | `handlers/admin_product_keys.go` |
| DELETE | `/v1/admin/products/{id}/keys/{keyId}` | `AdminProductKeysHandler.Revoke` | `handlers/admin_product_keys.go` |
| GET | `/v1/admin/products/{id}/models` | `AdminProductModelsHandler.List` | `handlers/admin_product_models.go` |
| PUT | `/v1/admin/products/{id}/models` | `AdminProductModelsHandler.Update` | `handlers/admin_product_models.go` |

**Per-product model allow-list (F-000015).** `GET`/`PUT /v1/admin/products/{id}/models` управляют allow-list'ом моделей + порядком вывода. PUT — replace: порядок массива `modelIds` = порядок вывода (`position`); пустой массив снимает ограничение (fail-open). Явный `ProductRepo.GetByID`-precheck → `404 PRODUCT_NOT_FOUND` (FK сам по себе 404 не даёт). Replace транзакционный (`FOR UPDATE` + dedup + `ON CONFLICT` + rollback). Существование модели в каталоге не валидируется. Гейт `requireAdmin`; регистрируется при `deps.ProductRepo != nil && deps.AllowedModelsRepo != nil`.

### Admin: config

| Метод | Путь | Handler | Файл |
|-------|------|---------|------|
| GET | `/v1/admin/config` | `AdminConfigHandler.List` | `handlers/admin_config.go` |
| PUT | `/v1/admin/config` | `AdminConfigHandler.Update` | `handlers/admin_config.go` |
| GET | `/v1/admin/microservices/health` | `AdminConfigHandler.MicroservicesHealth` | `handlers/admin_config.go` |
| POST | `/v1/admin/microservices/{name}/check` | `AdminConfigHandler.CheckMicroservice` | `handlers/admin_config.go` |
| POST | `/v1/admin/swanupdates/test-download` | `UpdatesHandler.TestDownload` | `handlers/updates.go` |

### Admin: model-prices (внутренний прайс, v0.33.0)

| Метод | Путь | Handler | Файл |
|-------|------|---------|------|
| GET | `/v1/admin/model-prices` | `AdminModelPricesHandler.List` | `handlers/admin_model_prices.go` |
| PUT | `/v1/admin/model-prices/{modelId...}` | `AdminModelPricesHandler.Update` | `handlers/admin_model_prices.go` |
| DELETE | `/v1/admin/model-prices/{modelId...}` | `AdminModelPricesHandler.Delete` | `handlers/admin_model_prices.go` |

Wildcard `{modelId...}` — канонические ID содержат `/` (`anthropic/claude-sonnet-4.6`), single-segment паттерн давал бы 404. PUT/DELETE дергают `Registry.Reload()` — горячий подхват прайс-снапшота; ошибка Reload не валит запрос (изменение уже в БД), но уходит в errorlog `REGISTRY_RELOAD_FAILED` (registry при этом работает на предыдущем снапшоте — keep-previous-on-error). GET обогащается закупкой Router'а через `ModelCatalogSource` (nil-able — поля omitempty).

### Admin: model-display-overrides (presentation labels, v0.49.0)

| Метод | Путь | Handler | Файл |
|-------|------|---------|------|
| GET | `/v1/admin/model-display-overrides` | `AdminModelDisplayOverridesHandler.List` | `handlers/admin_model_display_overrides.go` |
| GET | `/v1/admin/model-display-overrides/{modelId...}` | `AdminModelDisplayOverridesHandler.Get` | `handlers/admin_model_display_overrides.go` |
| PUT | `/v1/admin/model-display-overrides/{modelId...}` | `AdminModelDisplayOverridesHandler.Update` | `handlers/admin_model_display_overrides.go` |
| DELETE | `/v1/admin/model-display-overrides/{modelId...}` | `AdminModelDisplayOverridesHandler.Delete` | `handlers/admin_model_display_overrides.go` |

Override меняет только `ModelInfo.Name`/admin `displayName`; `modelId` остаётся машинным ключом для allow-list, billing и recommendations.

> Старые `PUT/DELETE /v1/admin/models/{provider}/{modelId}` (price overrides) и `GET/PUT /v1/admin/providers*` (ключи/прокси/активность) удалены в v0.33.0 — провайдеры управляются в админке самого Router'а.

### Admin: audit read API (T-000047 v0.19.0)

| Метод | Путь | Handler | Файл |
|-------|------|---------|------|
| GET | `/v1/admin/admin-actions` | `AdminActionsHandler.List` | `handlers/admin_actions.go` |
| GET | `/v1/admin/user-auth-events` | `AdminUserAuthEventsHandler.List` | `handlers/admin_user_auth_events.go` |

Оба поддерживают фильтры (action/event csv, target_kind/target_id, key_prefix, since/until) и пагинацию (page/size).

## Условная регистрация

Не все маршруты регистрируются всегда — некоторые гейтятся наличием соответствующего dependency:

| Группа | Гейт |
|--------|------|
| OTP, Feedback (создание + admin list) | `deps.InfraConnectClient != nil` |
| User-facing (`/v1/api/me*`, `/v1/feedback`) | `deps.UsersRepo != nil` |
| Admin users (`/v1/admin/users*`, `/v1/admin/user-keys/*`) | `deps.UsersRepo != nil` |
| Admin admins (`/v1/admin/admins*`, `/v1/admin/admin-keys/*`) | `deps.AdminsRepo != nil && deps.AdminKeysRepo != nil` |
| Admin products | `deps.ProductRepo != nil` |
| Admin product-keys | `deps.ProductRepo != nil && deps.ProductKeyService != nil` |
| Admin config | `deps.Config != nil` |
| Admin model-prices | `deps.ModelPricesRepo != nil && deps.Registry != nil` |
| Admin audit (`admin-actions`, `user-auth-events`) | `deps.AdminActionsRepo != nil` / `UserAuthEventsRepo != nil` |
| Chat (все `/v1/api/chat*` + `/v1/api/providers`, etc.) | `deps.Registry != nil && deps.Store != nil` |

В deps nil-уровне dev-сборки получаешь сервер с подмножеством endpoint'ов — это by design, чтобы можно было запустить с минимальной конфигурацией для smoke / в CI.

## Связанные документы

- [docs/my_api/](my_api/) — public-контракт каждого endpoint'а (`client-api.md` / `admin-api.md` + топики: request/response/errors/side effects)
- [docs/flows/](flows/) — flow-доки для сложных фич (auth-otp, auth-billing, audit-log, middleware-routing)
- [internal/transport/http/router.go](../internal/transport/http/router.go) — source of truth
- [internal/transport/http/middleware.go](../internal/transport/http/middleware.go) — middleware implementations
