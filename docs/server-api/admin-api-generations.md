# Admin API — Generations audit (аудит генераций ERF/EPF)

[← Индекс admin-контракта](admin-api.md)

Диагностический аудит попыток `POST /v1/api/reports/generate-erf` и
`POST /v1/api/processors/generate-epf` (T-000336, REQ-062). Каждая попытка
фиксируется **до разбора тела** (включая битый JSON и отклонённую валидацию)
с точными входными байтами. Тела запросов пользователей доступны **только
здесь** (admin-only) — в операционные логи они по-прежнему не пишутся.

Retention: config-ключ `generation_audit.retention_days` (дефолт 30, hot-reload
через `PUT /v1/admin/config`; ≤ 0 — чистка выключена). Чистка раз в час удаляет
записи старше вместе с сохранённым телом.

Статусы записи: `started` (попытка зафиксирована, исход ещё не записан —
застрявший `started` означает, что процесс умер посреди генерации) →
`succeeded` | `failed`.

---

## GET /v1/admin/generations

Список записей аудита, новейшие первыми, **без тел запросов**.

**Query-параметры** (все опциональны, комбинируются):

| Параметр | Значение |
|---|---|
| `type` | `erf` \| `epf` |
| `status` | `started` \| `succeeded` \| `failed` |
| `userId` | UUID пользователя |
| `sessionId` | UUID диалога |
| `requestId` | точный `X-Request-ID` |
| `from`, `to` | RFC3339, диапазон по `startedAt` |
| `limit` | 1..200, дефолт 50 |
| `offset` | дефолт 0 |

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "items": [
    {
      "id": 42,
      "requestId": "uuid",
      "type": "erf",
      "userId": "uuid",
      "productId": "uuid",
      "sessionId": "uuid, optional",
      "status": "failed",
      "httpStatus": 400,
      "errorCode": "ERF_INVALID_REQUEST, optional",
      "errorMessage": "string, optional",
      "bodySize": 177,
      "artifactName": "Отчет.erf, optional (успех)",
      "artifactSize": 6325,
      "artifactSha256": "hex, optional (успех)",
      "startedAt": "RFC3339",
      "finishedAt": "RFC3339, optional",
      "durationMs": 12
    }
  ],
  "total": 1
}
```

- `total` — общее число записей под фильтром (для пагинации).
- `bodySize` — размер тела; при `body_too_large` сами байты не сохранены,
  `bodySize` отражает прочитанное.

**Errors:** `400 INVALID_TIME_RANGE` — `from`/`to` не RFC3339.

---

## GET /v1/admin/generations/{id}

Деталь записи с точным телом запроса.

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "item": { "...та же форма, что в списке..." },
  "requestBodyB64": "base64 точных входных байт; опущено, если тело не сохранялось"
}
```

**Errors:** `400 MISSING_PARAMS` — невалидный id; `404 GENERATION_NOT_FOUND`.

**Side effects:** нет (read-only; секретные заголовки — `Authorization`,
`X-Product-Key`, `X-QC-Token` — в аудит не попадают вовсе).
