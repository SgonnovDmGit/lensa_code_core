# Admin API — Audit & observability (admin-actions / auth-events / error-logs / feedback)

> Часть admin-контракта. Индекс: [admin-api.md](admin-api.md).

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

