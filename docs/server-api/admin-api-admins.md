# Admin API — Admins & keys

> Часть admin-контракта. Индекс: [admin-api.md](admin-api.md).

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
  "key": "lens_ak_{prefix}_{secret} — первый ключ, выдаётся ОДИН раз",
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
  "key": "lens_ak_{prefix}_{secret} — выдаётся ОДИН раз",
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

