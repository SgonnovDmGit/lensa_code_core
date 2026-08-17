# Admin API — Products

> Часть admin-контракта. Индекс: [admin-api.md](admin-api.md).

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
      "compactionMaxContextTokens": "int — absolute-лимит контекста для авто-компактизации (R1 v0.69.0); 0 = off → fallback на pct × model_context_window. Для консоли: 100000",
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
- **`compactionMaxContextTokens` поддерживается с v0.69.0 (R1, T-000266)** — absolute-лимит контекста для авто-компактизации; 0 = off (fallback на `compactionThresholdPct × model_context_window`). int, ≥ 0

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

Выпускает новый product_key для продукта. **`fullKey` в ответе содержит plaintext `lens_pk_{prefix}_{secret}` — выдаётся ОДИН раз** (легаси `swan_pk_*`-ключи продолжают приниматься), в БД хранится только `bcrypt(secret)`. Status: `implemented`.

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
  "fullKey": "lens_pk_{prefix(8 hex)}_{secret(32 hex)} — единственный момент когда secret виден"
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

