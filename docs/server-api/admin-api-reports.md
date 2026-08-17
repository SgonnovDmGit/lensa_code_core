# Admin API — Reports

[← Индекс admin-контракта](admin-api.md)

Отчётная поверхность админки (F-000036, REQ-045 + REQ-047, `v0.74.0`): агрегаты usage/revenue для Dashboard и Reports — топы юзеров/моделей, дневные бакеты, product spend. Источник данных — `usage_stats`: **только успешные биллуемые AI-вызовы**; failed/non-billed попытки не имеют usage-строки и не надувают ни `aiCalls`, ни revenue (персистнутое failed-сообщение при этом входит в `newMessages`). С `v0.74.0` в usage входят и summarizer-вызовы компактизации.

## GET /v1/admin/reports/usage

Один агрегатный эндпоинт. Read-only; секции считаются отдельными SQL-запросами без транзакции — между ними могут долететь новые строки (live-данные, snapshot не атомарный).

**Query:**

| Параметр | Default | Описание |
|---|---|---|
| `from` | `to − 29 дней` | Начало диапазона, `YYYY-MM-DD`, **включительно** (в `tz`) |
| `to` | сегодня (в `tz`) | Конец диапазона, `YYYY-MM-DD`, **включительно** |
| `tz` | `UTC` | IANA-таймзона суточной нарезки и трактовки `from`/`to` (напр. `Europe/Moscow`) |
| `limit` | 5 | Размер top-списков (1..100). На `modelUsage` не влияет |

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "from": "2026-06-12",
  "to": "2026-07-11",
  "tz": "Europe/Moscow",
  "topUsers": [
    {"userId": "uuid", "displayName": "Ivan Petrov", "calls": 120,
     "promptTokens": 500000, "completionTokens": 40000, "totalTokens": 540000,
     "chargedCoins": 1234.5,
     "economics": {"chargedCoins": 1234.5, "chargedUsd": 2.74, "chargedRub": 246.9,
                   "routerCostUsd": 1.2, "routerCostRub": 108,
                   "marginUsd": 1.54, "marginRub": 138.9}}
  ],
  "topModelsByChargedCoins": [
    {"model": "anthropic/claude-fable-5", "displayName": "Claude Fable 5",
     "calls": 80, "promptTokens": 400000, "completionTokens": 30000, "totalTokens": 430000,
     "economics": {"chargedCoins": 900, "chargedUsd": 2, "chargedRub": 180,
                   "routerCostUsd": 1, "routerCostRub": 90,
                   "marginUsd": 1, "marginRub": 90}}
  ],
  "topModelsByTokens": [{"...": "та же структура"}],
  "modelUsage": [{"...": "та же структура, ВСЕ модели диапазона"}],
  "products": [
    {"productId": "uuid", "name": "query_console_1c", "calls": 100,
     "totalTokens": 500000, "chargedCoins": 1100}
  ],
  "daily": [
    {"date": "2026-07-10", "newSessions": 4, "newMessages": 52, "aiCalls": 37,
     "economics": {"chargedCoins": 300, "chargedUsd": 0.66, "chargedRub": 60,
                   "routerCostUsd": 0.3, "routerCostRub": 27,
                   "marginUsd": 0.36, "marginRub": 33}}
  ],
  "cacheSavings": {"userSavedCoins": 812.4, "purchaseSavedCoins": 430.1, "marginEffectCoins": -382.3}
}
```

**Секции:**

- **`topUsers[]`** — топ юзеров по `chargedCoins` (списание, коины). `displayName` — fallback-цепочка `users.display_name` → `infraconnect_id` → `userId`. usage-строки с `user_id = NULL` в топ не входят (нет identity); их деньги видны в model/product/daily-секциях. С `v0.78.0` (REQ-052) несёт `economics` — тот же блок `TurnEconomics`, что в model-секциях/`daily[]` (маржа по пользователю); верхнеуровневый `chargedCoins` остаётся на месте, внутри блока он дублируется.
- **`topModelsByChargedCoins[]` / `topModelsByTokens[]`** — топ-`limit` моделей по списанию / по `totalTokens`. `displayName` — из `model_display_overrides`, **omitempty**: нет override'а → поле опущено, клиент резолвит по своему каталогу моделей.
- **`modelUsage[]`** — **все** модели диапазона (без limit), сортировка `totalTokens` desc. Для weighted markup на экране моделей (REQ-047): `promptTokens`/`completionTokens` по каждой реально использованной модели.
- **Кэш-агрегаты (T-000295, `v0.75.0`):** в model-секциях и `daily[]` — `cacheReadTokens`/`cacheWriteTokens` (Σ prompt-токенов, прочитанных из / записанных в provider-кэш; оба ⊆ `promptTokens`). Оценка кэш-экономии по моделям: read-токены биллятся дешевле обычных prompt. **omitempty ⟺ >0** — поля отсутствуют у моделей/дней без кэш-активности (opaque-провайдеры отдают 0/0 — у них полей не будет; отсутствие = «кэш не наблюдался», не «кэша нет»).
- **Экономия кэша в деньгах (T-000309, REQ-053, `v0.80.0`):** в `modelUsage[]`/топах моделей — `cacheUserSavedCoins` (недосписано с пользователя) и `cachePurchaseSavedCoins` (экономия на закупке у Router); итоги периода — верхнеуровневый `cacheSavings` (`userSavedCoins`/`purchaseSavedCoins`/`marginEffectCoins = purchase − user`). **Формула и источник:** значения персистятся на settle каждого вызова по ставкам **момента вызова** (отчёт только суммирует — прошлые токены не умножаются на текущий прайс). Юзер-сторона = `EstimateCost(prompt, completion)` − cache-aware списание того же вызова (наш прайс с наценкой, включая per-model cache-ставки; «цена без кэша» = полная prompt-цена на все входные токены). Закупка = гипотетика `promptTokens × ₽-фид-ставка(input) + completionTokens × ₽-фид-ставка(output)` − фактический cache-aware `router_cost_rub_micro`; хранится в ₽ и конвертится в коины по **текущему** `pricing.coins_per_rub` (курс коина — дефиниционный якорь, практически стабилен). **Значения signed:** cache-write ×1.25 дороже input — ход, который только пишет кэш, даёт отрицательную «экономию»; `marginEffectCoins < 0` = скидка пользователю больше нашей выгоды. **omitempty:** per-model поля присутствуют ⟺ у модели была кэш-активность в периоде (нулевая экономия при живом кэше = честный `0`); `cacheSavings` отсутствует ⟺ кэш-активности не было вовсе. **История:** строки до `v0.80.0` несут нули — отчёт за прошлые периоды покажет экономию только по новым вызовам (бэкфилл невозможен: ставок на момент старых вызовов больше нет). Закупочная сторона `0` также когда Router не прислал ₽-cost / модели нет в ₽-фиде (гипотетику закупки честно посчитать нельзя).
- **`products[]`** — product spend. Атрибуция `usage_stats → sessions.product_id` (JOIN); usage-строки с осиротевшим `session_id` (hard-delete сессии) собираются в бакет **без `productId`** с `name: "unattributed"` — деньги из сумм не теряются.
- **`daily[]`** — дневные бакеты в `tz`, по возрастанию даты. `newSessions` — созданные сессии (последующий soft-delete не вычитает); `newMessages` — только `user_text`/`assistant_text` (tool-капсулы и system не считаются; персистнутые failed-ходы входят); `aiCalls` — строки `usage_stats` (= биллуемые AI-вызовы, включая компактизацию).

**`economics`** — та же структура `TurnEconomics`, что в admin session economics (см. [admin-api-users.md](admin-api-users.md)): `chargedCoins` — измеренное списание; `chargedUsd`/`chargedRub` — конвертация по **текущему** курсу `pricing.coins_per_usd`/`coins_per_rub` (live-вью: курс менялся → исторические $/₽ приблизительны); `routerCostUsd`/`routerCostRub` — COGS из исторических Router-значений (точные); `margin* = charged − routerCost`. `economics` = `null`/опущено, когда денежных данных нет вовсе (напр. день без AI-вызовов). Нюанс: `routerCost* = 0` при `chargedCoins > 0` встречается у старых строк, где Router не прислал COGS.

**Семантика пустоты:** все коллекции — `[]`, не `null`. Дни без активности в `daily[]` **не выдаются** — отсутствующий день означает «нет данных за день», клиент заполняет календарную ось нулями сам; внутри присутствующего дня `0` — измеренный ноль.

**Errors:**
- `INVALID_TZ` (400) — неизвестная IANA-таймзона
- `INVALID_RANGE` (400) — не-`YYYY-MM-DD` либо `from > to`
- `RANGE_TOO_WIDE` (400) — диапазон шире 366 дней
- `DB_ERROR` (500)
