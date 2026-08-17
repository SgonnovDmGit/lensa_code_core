# Admin API — Core (server-info / stats / recommendations / prompt reads)

> Часть admin-контракта. Индекс: [admin-api.md](admin-api.md).

# Admin API (bearer `swan_ak_*`)

## GET /v1/admin/server-info

Идентификатор сервера для Test Connection в админ-клиенте и compatibility-check'а версии. Проверяет одним запросом: сервер живой, bearer-формат работает, admin-ключ валиден.

**Response (200):**
```json
{
  "serviceInfo": {"success": true},
  "version": "0.23.0",
  "name": "LenSA Code API",
  "buildCommit": "1a2b3c4d5e6f...",
  "uptime": 12345,
  "features": ["two-key-auth", "otp", "products", "admins", "feedback", "ai-router", "model-restriction", "model-display-overrides", "updates"]
}
```

| Поле | Тип | Описание |
|------|-----|----------|
| `version` | `string` | SemVer-версия сервера (`MAJOR.MINOR.PATCH`), source-of-truth — `internal/version/version.go` |
| `name` | `string` | Display-имя сервера (бренд «LenSA Code», с `v0.78.0`; до того — имя репозитория `swancode_server`). Не матчить строково — только для показа |
| `buildCommit` | `string` | Git SHA docker-сборки (с `v0.86.0`, T-000322). Вне docker-билда (локальный `go run`) — `"unknown"` |
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
  "errorCount": 7,
  "totalUserBalanceCoins": 12345.678901
}
```

| Поле | Тип | Описание |
|------|-----|----------|
| usersCount | int | Активные пользователи (без soft-deleted) |
| errorCount | int | Записей в error_logs |
| totalUserBalanceCoins | float, optional | Суммарный баланс всех кошельков проекта в **коинах** (десятичные). Источник — S2S-агрегат Swan_Billing `GET /v1/s2s/wallets/aggregate` (отдаёт micro, делим на 1e6). **omitempty:** поле опускается, если Billing недоступен/не сконфигурён — stats при этом не падает (dashboard рисует n/a). |

> `totalUserBalanceCoins` возвращён в v0.66.1 (T-000187/REQ-033) после того, как Swan_Billing получил агрегатный эндпоинт (REQ-005, v0.10.0). Ранее (v0.41.0–v0.65.x) поле было снято — у Billing не было «суммы по всем кошелькам».

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

