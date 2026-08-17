# Admin API — Microservices (health / swanupdates / querycrypt)

> Часть admin-контракта. Индекс: [admin-api.md](admin-api.md).

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
    {"name": "ai-router", "status": "online", "httpStatus": 200, "uptimeSec": 86400},
    {"name": "billing", "status": "not_configured", "errorCode": "BILLING_NOT_CONFIGURED"}
  ]
}
```

`status` и `items[].status`: `online`, `degraded`, `offline`, `not_configured`. Детальная причина остаётся в `errorCode`/`message`; для сырого `ok|auth_failed|unreachable|bad_config|unexpected_status` используйте точечный `POST /v1/admin/microservices/{name}/check`.

`items[].uptimeSec` (int64, опционально; T-000303, REQ-051) — секунды жизни процесса микросервиса. **Источник — тело probe-ответа самого МС** (тот же запрос, что определяет статус; отдельного опроса нет): `ai-router` — `uptime_sec` из его `GET /v1/server-info`, `infraconnect` — `uptimeSec` из его `GET /v1/server-info`, `swanupdates` — `data.uptimeSec` из его `GET /v1/server-info` (T-000340, v0.85.0: probe переключён с фиктивного `updates/check` на их server-info). У `billing` probe-эндпоинт времени жизни пока не отдаёт — поле отсутствует (downstream-REQ отправлен; появится без правок админки). Семантика присутствия: **отсутствие ≠ ноль** — нет поля = «аптайм неизвестен» (`not_configured`/`offline`/МС без поддержки), `0` = «процесс только что стартовал». Значение — **снапшот на момент замера** (`checkedAt` агрегата): между тиками 30-секундного кэша оно не растёт, и рестарт МС внутри окна кэша виден только со следующего тика (или `?refresh=true`) — свежий маленький `uptimeSec` при прежнем `online` и есть признак рестарта. `httpStatus`/`errorCode` не изменились.

Поле `uptimeSec` с той же семантикой присутствует и в ответе точечного `POST /v1/admin/microservices/{name}/check` (status `ok`).

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

