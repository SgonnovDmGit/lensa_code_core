# Admin API — Pricing (model-prices / display-overrides / db reset)

> Часть admin-контракта. Индекс: [admin-api.md](admin-api.md).

## Удалено в v0.33.0: GET/PUT /v1/admin/providers*, PUT/DELETE /v1/admin/models/{provider}/{modelId}

Cutover F-000007: ключи, прокси и активность провайдеров управляются в админке самого Swan_AiRouter; серверная таблица `providers` снесена (миграция 017). Price-override'ы с осью `{provider}` заменены поверхностью внутреннего прайса `/v1/admin/model-prices` (ниже).

---

## GET /v1/admin/model-prices

Весь внутренний прайс моделей (источник биллинга) + **продажные (sell)** цены Router'а для контроля маржи.

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "prices": [
    {
      "modelId": "anthropic/claude-sonnet-4.6",
      "displayName": "Claude Sonnet 4.6",
      "promptPriceCoins": 0.005,
      "completionPriceCoins": 0.020,
      "cacheReadPriceCoins": 0.0005,
      "cacheWritePriceCoins": 0.00625,
      "cacheSource": "derived",
      "sellInputPer1m": 3300000,
      "sellOutputPer1m": 16500000,
      "sellInputPer1mRub": 257400000,
      "sellOutputPer1mRub": 1287000000,
      "sellCacheReadPer1m": 330000,
      "sellCacheWritePer1m": 4125000,
      "sellCacheReadPer1mRub": 25740000,
      "sellCacheWritePer1mRub": 321750000
    }
  ]
}
```

Поля:
- `promptPriceCoins` / `completionPriceCoins` — наша цена, **коины за токен** (float). Источник Hold/Settle биллинга и цен в `GET /v1/api/models`.
- `cacheReadPriceCoins` / `cacheWritePriceCoins` (T-000286 `v0.73.0`) — **конкретные cache-цены** (prompt caching, REQ-013): коины за cache-read / cache-write токен. Автозаполняются при upsert без явных значений и при `refresh-sell` (с T-000313 `v0.79.1` — от per-model rub-ставки Router-фида × `coins_per_rub × markup`; ось без ставки в фиде — откат ×0.10 / ×1.25 от prompt-цены; до v0.79.1 откат применялся всегда); админ может задать явно через PUT. Settle-формула хода: `(prompt − read − write)×input + read×cacheRead + write×cacheWrite + completion×output`.
- `cacheSource` (T-000298 `v0.77.0`, REQ-049) — происхождение cache-цен строки: `"manual"` (заданы админом явно через PUT; `refresh-sell` их **не перетирает**) / `"derived"` (автодеривация; с T-000313 `v0.79.1` — от per-model ставки Router-фида, см. выше). До Router v0.57.0 per-model cache-ставок в фиде не было и деривация ×0.10/×1.25 была тождественна его формуле продажи; ANNOUNCE-013 отдал фактические ставки (×0.008..×1.0) — derived-цены теперь следуют им, отрицательная маржа кэш-части исключена; фактический COGS кэша и так меряется по cache-aware `cost_micro` каждого запроса.
- `sellInputPer1m` / `sellOutputPer1m` — **продажная** ставка Router'а = что **мы платим** Router'у (его закупка + наценка), **micro-USD за 1M токенов**. Это наша себестоимость для контроля маржи (revenue в коинах vs sell), НЕ закупка Router'а у апстрима. (`omitempty`: отсутствуют, пока Router не отдаёт sell в consumer-каталоге (REQ-002) или модель не найдена).
- `sellInputPer1mRub` / `sellOutputPer1mRub` — та же продажная ставка в **native-₽** (micro-RUB за 1M токенов, курс ЦБ считает Router; REQ-011/REQ-044, v0.67.0). Позволяет считать ₽/coins по рублю Router'а, а не `USD × наш курс`. `omitempty` при нуле/нет-курса ЦБ — **асимметрия с USD**: USD при нуле present (`0`=бесплатно), RUB при нуле опускается («RUB отсутствует» ≠ «бесплатно»).
- `sellCacheReadPer1m` / `sellCacheWritePer1m` (+`...Rub`) (T-000307 `v0.79.0`, REQ-054) — **закупочная ставка кэша**: почём Router продаёт нам cache-read / cache-write, micro-USD (и native micro-RUB) за 1M токенов. **Источник:** per-model ставки consumer-фида Router (`sell_cache_read_per_1m`/`sell_cache_write_per_1m` + `_rub`, Router v0.57.0 — его ответ на наш REQ-014: реальное отношение cache/input гуляет ×0.008..×1.0, глобальных множителей как источника истины больше нет). Ось, которую фид не отдал (нулевая/неопубликованная апстримом ставка), деривится от `sellInputPer1m(Rub)` откатом ×0.10 / ×1.25 — тождественным биллинг-фолбэку самого Router'а для таких моделей. Присутствие — с той же асимметрией, что у sell-полей: USD present при нуле (0 = бесплатно), RUB опущен, когда Router не отдал рубль (отсутствие ≠ бесплатно); поля отсутствуют вместе с соответствующими sell-полями.
- `displayName` — из Router-каталога или admin override `model_display_overrides` (`omitempty`).

---

## POST /v1/admin/model-prices/refresh-sell

Материализация внутреннего прайса (`model_prices`) из **рублёвой** Router-sell (rouble-first, T-000235, v0.67.0). Перечисляет все модели каталога с известной RUB-ставкой. **Dry-run по умолчанию** — `?apply=true` записывает.

Цена считается с **вбитым markup** (`model_prices` = final, наценка в рантайме к ним не применяется): `newPromptCoins/токен = sell_input_rub × coins_per_rub × markup / 1e12`. `coins_per_rub` / `markup` — из `server_config` (`pricing.coins_per_rub` / `pricing.markup`).

**Query:** `apply` — `true` → запись + reload registry; иначе (или отсутствует) → preview без изменений БД.

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "applied": false,
  "items": [
    {
      "modelId": "anthropic/claude-sonnet-4.6",
      "displayName": "Claude Sonnet 4.6",
      "oldPromptCoins": 0.005,
      "oldCompletionCoins": 0.020,
      "newPromptCoins": 0.002574,
      "newCompletionCoins": 0.012870,
      "oldCacheReadCoins": 0.0005,
      "oldCacheWriteCoins": 0.00625,
      "newCacheReadCoins": 0.0002574,
      "newCacheWriteCoins": 0.0032175,
      "sellInputRub": 257400000,
      "sellOutputRub": 1287000000,
      "marginPct": 0.5
    }
  ]
}
```

Поля item:
- `oldPromptCoins` / `oldCompletionCoins` — текущая цена в `model_prices` (0, если строки ещё нет).
- `newPromptCoins` / `newCompletionCoins` — что будет записано (коины/токен, markup вбит).
- `oldCacheReadCoins` / `oldCacheWriteCoins` / `newCacheReadCoins` / `newCacheWriteCoins` (T-000290, v0.73.1, REQ-048) — cache-цены: old — текущие из прайса, new — что применит `apply`. С v0.77.0 (T-000298, REQ-049): у строк с `cacheManual: true` apply cache-цены **не трогает** — new честно равны old. У прочих new = автодеривация: с T-000313 `v0.79.1` — **per-model rub-ставка Router-фида × `coins_per_rub × markup`** (та же конвертация, что `newPromptCoins`); ось без ставки в фиде — откат ×0.10 / ×1.25 от новой prompt-цены. Отдельных RUB-source полей для кэша в ответе нет — закупочные ставки видны в `GET /v1/admin/model-prices` (`sellCache*`).
- `cacheManual` (T-000298 `v0.77.0`, REQ-049, `omitempty`) — cache-цены строки заданы админом явно; apply их сохраняет.
- `sellInputRub` / `sellOutputRub` — native-₽ Router-sell (micro-RUB за 1M), источник расчёта.
- `marginPct` — маржа поверх себестоимости, `1 − 1/markup` (при `markup=2` → `0.5`).
- `applied` — `false` = preview (БД не тронута); `true` = записано.

**Errors:**
- `CATALOG_UNAVAILABLE` (503) — Router-каталог/конфиг недоступен.

**Side effects (только при `apply=true`):**
- Upsert строк `model_prices` per модель + `Registry.Reload` (горячий подхват прайс-снапшота).
- Cache-цены (T-000286): у строк `cacheSource: "derived"` **пересчитываются** (T-000313 `v0.79.1`: от per-model ставки фида, фолбэк ×0.10 / ×1.25 от новой prompt-цены); у строк `cacheSource: "manual"` (T-000298, v0.77.0) **сохраняются** — явная правка админа не затирается авто-пересчётом.

---

## PUT /v1/admin/model-prices/{modelId}

Установка/обновление внутренней цены модели. `{modelId}` — канонический ID `vendor/name`, путь матчится **wildcard'ом** (слеши в ID передаются как есть, URL-encoding не требуется): `PUT /v1/admin/model-prices/anthropic/claude-sonnet-4.6`.

**Request:**
```json
{
  "promptPriceCoins": 0.005,
  "completionPriceCoins": 0.020,
  "cacheReadPriceCoins": 0.0005,
  "cacheWritePriceCoins": 0.00625
}
```

`promptPriceCoins`/`completionPriceCoins` обязательны, ≥ 0, коины за токен. `cacheReadPriceCoins`/`cacheWritePriceCoins` (T-000286 `v0.73.0`) опциональны: отсутствуют или `0` → **автозаполнение** (T-000313 `v0.79.1`: per-model rub-ставка Router-фида × `coins_per_rub × markup`, ось без ставки — откат ×0.10 / ×1.25 от prompt-цены) + `cacheSource: "derived"`; явное ненулевое значение хотя бы одной → строка помечается `cacheSource: "manual"` и `refresh-sell` её cache-цены больше не перетирает (T-000298, REQ-049); отрицательные → `INVALID_PRICE`. **Семантика нулей (REQ-048):** `0`/omitted/null в PUT означают «авто», НЕ «бесплатно» — истинно-нулевая (free) cache-цена в контракте непредставима; «недоступно» как отдельного состояния нет — cache-цены существуют у любой строки прайса (деривация гарантирует ненулевые значения при ненулевой prompt-цене).

**Response 200** (cache-цены — итоговые: явные либо автозаполненные):
```json
{
  "serviceInfo": {"success": true},
  "modelId": "anthropic/claude-sonnet-4.6",
  "promptPriceCoins": 0.005,
  "completionPriceCoins": 0.020,
  "cacheReadPriceCoins": 0.0005,
  "cacheWritePriceCoins": 0.00625
}
```

**Ошибки:**
- 400 `MISSING_PARAMS` — пустой modelId.
- 400 `INVALID_REQUEST` — не указан `promptPriceCoins` или `completionPriceCoins`.
- 400 `INVALID_PRICE` — отрицательное значение.

**Side effects:** `Registry.Reload()` — следующий ход уже биллится по новой цене.

---

## DELETE /v1/admin/model-prices/{modelId}

Снятие цены. Модель остаётся видимой в каталоге с нулевой ценой; её ходы **не тарифицируются** (fail-open, в логе warn `BILLING_SKIPPED`).

**Response 200:** `{"success": true}` + `Registry.Reload()`.

---

## GET /v1/admin/model-display-overrides

Список admin-defined labels моделей (v0.49.0, T-000198). Override меняет только человекочитаемый `name` в `GET /v1/api/models` и `displayName` в `GET /v1/admin/model-prices`; технический `modelId` не меняется.

С v0.84.0 (T-000333) строка дополнительно несёт маркетинговые атрибуты витрины сайта продаж: `description`, `descriptionEn` (v0.85.0, T-000341 — английская локаль описания) и `tier` (все **omitempty** — не заданы → поля опущены). Модель с заданным `tier` отдаётся в S2S-эндпоинте `GET /v1/s2s/sales/models` (контракт — sales-api.md; `description` → `descriptionI18n.ru`, `descriptionEn` → `descriptionI18n.en`).

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "overrides": [
    {
      "modelId": "openai/gpt-5",
      "displayName": "GPT-5",
      "description": "Для наиболее сложных рассуждений.",
      "tier": "flagship",
      "createdAt": "2026-06-26T10:00:00Z",
      "updatedAt": "2026-06-26T10:00:00Z"
    }
  ]
}
```

---

## GET /v1/admin/model-display-overrides/{modelId}

Чтение override одной модели. `{modelId}` — wildcard `vendor/name`.

**Response 200:** `{ "serviceInfo": {"success": true}, "override": { ... } }`

**Errors:** `404 MODEL_DISPLAY_OVERRIDE_NOT_FOUND`.

---

## PUT /v1/admin/model-display-overrides/{modelId}

Создание/обновление display-name override + маркетинговых атрибутов витрины (T-000333).

**Request:**
```json
{
  "displayName": "Claude Sonnet 4.6",
  "description": "string, optional — маркетинговое описание для сайта продаж (локаль ru), ≤ 500 символов",
  "descriptionEn": "string, optional — английская локаль описания (v0.85.0, T-000341), ≤ 500 символов",
  "tier": "string, optional — enum: flagship | advanced | balanced | basic (v0.84.1), либо \"\" для сброса"
}
```

`displayName` trim'ится, допустимая длина — 1..200 символов. Обновляется всегда (обязательное поле).

**Partial update semantics** (`description` / `descriptionEn` / `tier`):
- Поле **отсутствует в теле** → значение в БД не меняется
- Поле передано как **`""`** → значение сбрасывается (NULL); сброс `tier` снимает модель с витрины `GET /v1/s2s/sales/models`
- Непустое значение → устанавливается (trim)

**Response 200:** `{ "serviceInfo": {"success": true}, "override": { ... } }`

**Errors:** `400 INVALID_JSON`, `400 INVALID_DISPLAY_NAME`, `400 INVALID_DESCRIPTION`, `400 INVALID_TIER`.

**Side effects:**
- Задание `tier` публикует модель на витрине сайта продаж (при наличии строки в model_prices); сброс — снимает.

---

## DELETE /v1/admin/model-display-overrides/{modelId}

Удаление override. После удаления `GET /v1/api/models` снова отдаёт имя из Router-каталога. Вместе со строкой удаляются и маркетинговые атрибуты — модель снимается с витрины `GET /v1/s2s/sales/models` (T-000333).

**Response 200:** `{"success": true}`

---

## DELETE /v1/admin/database/reset

Пересоздание БД (только для разработки). Удаляет все таблицы и применяет миграции заново.

**Response 200:**
```json
{
  "serviceInfo": {"success": true}
}
```

---

