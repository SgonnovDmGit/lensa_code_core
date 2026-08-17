# Client API — Auth & User

> Часть client-контракта LenSA Code Server. Индекс: [client-api.md](client-api.md).

# OTP-аутентификация (требует X-Product-Key)

Authn-события привязываются к product_id — каждый OTP-запрос должен идти с заголовком `X-Product-Key: swan_pk_*`. Authorization-заголовок отсутствует (юзера ещё нет).

> **Легаси-канал (дремлет).** Боевой путь получения пользовательского ключа — per-user ключ платформы InfraConnect (`lens_live_*`/`lens_test_*`, F-000029); OTP-выпуск серверо-локального `swan_uk_*` сохраняется для обратной совместимости, транспортный пул платформой не заполняется.

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
  "codeLength": 6,
  "transport": "flashcall",
  "expiresAt": "2026-07-11T12:05:00Z"
}
```

**Поля `transport` / `expiresAt` (T-000287, опциональные, v0.73.1, ANNOUNCE-002 InfraConnect):** `transport` — **фактически использованный** транспорт доставки (`flashcall` | `sms` | `telegram` | `email`-подобные, набор на стороне платформы). При автофолбеке платформы (провайдер упал → код доставлен другим транспортом) значение меняется от ответа к ответу — клиент читает его из **каждого** ответа и не кеширует, иначе экран скажет «введите код из звонка», когда пришла SMS. `expiresAt` (RFC3339) — срок жизни кода. Оба `omitempty` — отсутствуют, если платформа их не сообщила.

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
- `INVALID_CODE` (401) — OTP неверен или истёк
- `OTP_TOO_MANY_ATTEMPTS` (429) — превышен лимит неверных попыток (5, T-000287/v0.73.1): код погашен, повторный verify даже с ВЕРНЫМ кодом вернёт `INVALID_CODE` — уведите пользователя на повторный `otp/send` («Слишком много попыток. Запросите новый код»)
- `USER_DELETED` (403) — аккаунт пользователя удалён
- `DB_ERROR` (500) — ошибка при UPSERT пользователя или выпуске ключа
- `RATE_LIMIT_EXCEEDED` (429) — платформенный rate-limit OTP-канала (T-000339, v0.85.0). Тело несёт `retryAfterSeconds` (+ HTTP-заголовок `Retry-After`) — подождите указанное время и повторите; НЕ ретраить в цикле. Не путать с `retryAfter` кода `OTP_ALREADY_SENT` — это разные поля разных кодов. Применим к `otp/send`, `otp/verify` и `/v1/feedback`

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

