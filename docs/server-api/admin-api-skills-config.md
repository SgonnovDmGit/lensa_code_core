# Admin API — Skills & Config

> Часть admin-контракта. Индекс: [admin-api.md](admin-api.md).

# Admin: Skills — доменные knowledge-паки (F-000033, v0.61.0)

Skills — доменные знания, подгружаемые моделью **по требованию** (progressive disclosure): каталог `{name, description}` активных скиллов продукта инжектится в системный промпт (дёшево), тело модель тянет сама server-executed тулом `load_skill(name)` — результат оседает в истории диалога как `tool_result` (клиент вызова **не видит**, клиентский контракт не меняется). Повторная загрузка гасится dedup'ом tool_result (v0.53.0). Выключенный (`isActive=false`) скилл пропадает из каталога, `load_skill` по нему возвращает модели inline-ошибку `SKILL_NOT_FOUND` со списком доступных.

Хранение — таблица `skills` по модели промптов: `template`/`description` (живые, правятся админом) + `defaultTemplate`/`defaultDescription` (каноны для diff/reset). Дефолты сидятся startup-reconcile'ом из файлов репо `skills_default/<product>/<name>.md` (`description` — из frontmatter файла); reconcile синхронит live-поле с каноном, только пока оно не правлено вручную (REQ-041 распространил admin-editable-семантику `template` на `description`). Стартовый набор — 3 консольных пака: `buh_skill`, `zup_skill`, `trade_skill`.

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
  "defaultDescription": "Запросы к бухгалтерским данным — ...",
  "template": "# Skill: бухгалтерские запросы...",
  "defaultTemplate": "# Skill: бухгалтерские запросы...",
  "isActive": true,
  "updatedAt": "2026-07-04T12:00:00Z"
}
```

`template != defaultTemplate` (или `description != defaultDescription`) → поле правилось вручную через PUT; админка показывает diff и «Откатить к дефолту». `defaultDescription` (REQ-041) — канон описания, аналог `defaultTemplate`.

**Errors:** `MISSING_NAME` (400), `SKILL_NOT_FOUND` (404).

## PUT /v1/admin/skills/{name}

Обновление тела (`template`) и опц. описания (`description`) скилла (REQ-041, `v0.63.0`). Status: `implemented`.

**Query parameters:** `productId` — опциональный.

**Request:**
```json
{
  "template": "Новый текст тела скилла...",
  "description": "Новое when-to-use описание (опционально)"
}
```

- `template` — обязателен, непустой.
- `description` — **опционально** (REQ-041). Отсутствует / пустая строка → текущее описание **не трогается** (частичный апдейт). Непустое → перезаписывает live `description`.

**Партиальная семантика и reconcile:** live `description` теперь admin-editable (как `template`). Пока `description == defaultDescription` (не правлен), startup-reconcile синхронит его с frontmatter файла `skills_default/`; после ручной правки через PUT reconcile его **не перетирает** (тот же CASE-паттерн, что у `template`/`defaultTemplate`). Для **ad-hoc-скилла** (нет файла в `skills_default/`) reconcile не участвует — правка `description` живёт бессрочно. `POST /v1/admin/skills/{name}/reset` откатывает **и** `template`, **и** `description` к их каноническим `default*`. `defaultTemplate`/`defaultDescription` через API не редактируются (источник истины — файл / момент создания).

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

Сброс `template` к `defaultTemplate` **и** `description` к `defaultDescription` (полный откат к канону, REQ-041; атомарный `UPDATE...RETURNING`, идемпотентен). Status: `implemented`.

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
- `infraconnect.validate_timeout_seconds` (int, дефолт 3, `v0.65.1`) — жёсткий дедлайн (сек) на S2S round-trip валидации per-user ключа (T-000236, hardening против DNS-провала хоста). Горячий auth-путь (validate на каждом запросе за fresh-TTL кэшем) при провале резолвинга иначе висит до общего http-timeout клиента (env `INFRACONNECT_TIMEOUT_SEC`, дефолт 10с) на **каждом** запросе → клиенты ловят зависание. Короткий дедлайн отдаёт быстрый `503 KEY_VALIDATION_UNAVAILABLE` (либо grace-фолбэк `validate_grace_seconds`, если ключ ещё в grace-окне). Оборачивает только round-trip — кэш-хиты дедлайну не подлежат. Глобальный, hot-reload. `0` = отдельного дедлайна нет (действует общий http-timeout); < 0 / нечисло → fallback 3.
- `billing.base_url` (string, `v0.41.0`) — base URL микросервиса Swan_Billing (со схемой `http(s)://`). Источник истины конфига доступа к биллингу; env `BILLING_BASE_URL` — только разовый seed на первом старте. **Hot-reload:** S2S-клиент пересобирается без рестарта через общий holder (money-path + admin read-proxy). Невалидная схема → `400 INVALID_BILLING_CONFIG`, не сохраняется. Пустой — money-path → `503 BILLING_NOT_CONFIGURED`.
- `billing.server_key` (string, `v0.41.0`) — X-Server-Key авторизации к Swan_Billing (S2S). Hot-reload как у `base_url`. **В `GET /v1/admin/config` маскируется** (`***`+last4; пустое → `(empty)`).

**Errors (config hot-reload):**
- `INVALID_AIROUTER_CONFIG` (400) — `airouter.base_url` без схемы `http(s)://`. Значение не персистится, работающий RouterClient не тронут.
- `INVALID_SWANUPDATES_CONFIG` (400) — `swanupdates.base_url` без схемы `http(s)://`. Значение не персистится, работающий update-клиент не тронут.
- `INVALID_INFRACONNECT_CONFIG` (400) — `infraconnect.base_url` без схемы `http(s)://`. Значение не персистится, работающий InfraConnect-клиент не тронут.
- `INVALID_BILLING_CONFIG` (400) — `billing.base_url` без схемы `http(s)://`. Значение не персистится, работающий Billing-клиент не тронут.

> Пустые `airouter.*` (БД + env) → сервер в деградации: chat/models/balance → `503 AI_NOT_CONFIGURED`. Пустые `swanupdates.*` → update-эндпоинты → `503 UPDATES_NOT_CONFIGURED` (канал безопасно спит). Пустой `infraconnect.base_url` → OTP/feedback → `503 INFRACONNECT_NOT_CONFIGURED`. Пустой `billing.base_url` → money-path (chat/tool-retry/tool-results) → `503 BILLING_NOT_CONFIGURED` (hard-dependency).
>
> При **недоступности** (а не отсутствии конфига) InfraConnect валидация per-user ключа сначала пытается grace-фолбэк (`infraconnect.validate_grace_seconds`, дефолт 10 мин) на недавно проверенный ключ, и только если grace не покрывает — отвечает `503 KEY_VALIDATION_UNAVAILABLE`. Это сглаживает короткие моргания IC, не пуская их в клиент. Дополнительно `infraconnect.validate_timeout_seconds` (дефолт 3с) ограничивает сам round-trip validate: при DNS-провале хоста клиент получает быстрый `503` (или тот же grace-фолбэк), а не висит до общего http-timeout на каждом запросе.

---

