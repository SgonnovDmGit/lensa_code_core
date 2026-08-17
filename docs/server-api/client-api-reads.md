# Client API — Data reads (providers / models / catalog / balance / recommendations)

> Часть client-контракта. Индекс: [client-api.md](client-api.md).

# Public API — Получение данных (bearer `swan_pk_*` или `swan_ak_*`)

## GET /v1/api/providers  (deprecated)

**Deprecated (T-000329, v0.83.0): понятие провайдера убрано из контракта** — сервер
единый провайдер, выбирать не из чего. Эндпоинт сохранён толерантным для старых
клиентов и отдаёт один элемент — каноническую атрибуцию `LenSA` (до v0.83.0 —
`SwanCode`). Новым клиентам эндпоинт не нужен.

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "providers": ["LenSA"]
}
```

---

## GET /v1/api/models  (deprecated-форма: `GET /v1/api/models/{provider}`)

Канон — `GET /v1/api/models` **без сегмента** (T-000329, v0.83.0 — понятие провайдера
убрано из контракта). Форма `/{provider}` — deprecated-толерантная: сохранена для
старых клиентов, значение сегмента игнорируется. Ниже — общий контракт ответа.



Список моделей. Каталог единый, источник — Router; поле `provider` ответа — атрибуция
канона `LenSA` (display-строка, клиенты её не матчат). ID моделей — канонический
формат `vendor/name`.

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "provider": "LenSA",
  "models": [
    {
      "id": "anthropic/claude-sonnet-4.6",
      "name": "Claude Sonnet 4.6",
      "subProvider": "anthropic",
      "contextLength": 1000000,
      "promptPriceCoins": 0.003,
      "completionPriceCoins": 0.015,
      "promptPriceCoinsPer1M": 3000,
      "completionPriceCoinsPer1M": 15000,
      "supportsTemperature": false,
      "supportsTools": false,
      "available": false,
      "unavailableReason": "upstream_dead"
    }
  ]
}
```

**Поле `contextLength` (T-000315, REQ-028, v0.80.0):** окно контекста модели в токенах — данные для **пикера моделей** (подпись/сортировка «сколько влезает»). **Всегда present** (с v0.80.0; раньше `omitempty` съедал ключ): `0` = «окно неизвестно» (Router-каталог без `context_window_tokens` для этой модели) — клиент показывает «неизвестно», не трактует как «0 токенов». Наполнение кольца контекста живого диалога рисуйте НЕ от этого поля, а от `contextFill` ответов чата (см. `client-api-chat.md`) — там та же величина плюс реальный порог компактизации.

**Поле `supportsTemperature` (T-000143, опциональное, v0.60.2):** `false` — модель задепрекейтила параметр `temperature` (Anthropic claude-5-семейство и т.п.): Router его такой модели не отправляет (self-heal с Router v0.45.0), настройка не имеет эффекта — клиенту рекомендуется прятать/дизейблить ползунок температуры для таких моделей. `true` — модель принимает `temperature`. `omitempty`: поле отсутствует, если Router его не сообщил (старая версия Router) — считать «неизвестно», UI не менять. Слать `temperature` в `/chat` безопасно в любом случае — на депрекейт-модели Router его молча дропнет.

**Поле `supportsTools` (T-000142, опциональное, v0.78.0):** `false` — модель не поддерживает native tool_use: сервер **не кладёт tools в запрос** такой модели (тихий гейт состава на всём turn-семействе), т.е. AI-ход пройдёт, но модель не сможет вызывать инструменты — в tool-продукте клиенту рекомендуется **дизейблить** такую модель в пикере (не прятать — иначе молча ломается сохранённый выбор). `true` — тулы декларируются как обычно. `omitempty`: поле отсутствует — Router его не сообщил — считать поддерживающей (гейт не срабатывает, состав запроса прежний).

**Поля `available` / `unavailableReason` (T-000289, опциональные, v0.73.1, ANNOUNCE-011 Router):** `available: false` — модель прямо сейчас не обслуживается ни одним провайдером (снята с апстрима); она **остаётся в списке** — клиенту рекомендуется дизейблить пункт селектора, а не прятать (иначе молча ломается сохранённый выбор модели). `unavailableReason` — причина (сейчас единственное значение `upstream_dead`), присутствует только при `available: false`. `omitempty`: поле `available` отсутствует — Router его не сообщил (старая версия) — считать доступной. Правда считается по запросу: попытка чата на `available: false` модели может и пройти (апстрим ожил между проверками); мёртвая модель в чате возвращает `503 MODEL_UNAVAILABLE`.

**Поля цен (v0.33.0 — breaking; per-1M добавлены в v0.40.0):**
- `promptPriceCoins` / `completionPriceCoins` — coins per token, **внутренний прайс сервера** (источник биллинга, админ-управление через `/v1/admin/model-prices`). Модель без выставленной цены — нули (ход не тарифицируется).
- `promptPriceCoinsPer1M` / `completionPriceCoinsPer1M` (v0.40.0) — те же цены за 1M токенов (`×1e6`), для читаемости. На этом (auth'd) эндпоинте отражают **ручной** прайс (`model_prices`); авто-цены дневного каталога Router×курс заполняются на публичном `GET /v1/models/catalog`.
- USD-поля `promptPrice`/`completionPrice` и `priceOverridden` **удалены**: коин-цены с USD не коррелируют по определению (внутренняя валюта); закупка в USD доступна только админу через `GET /v1/admin/model-prices`.

**Display-name override (v0.49.0):** `name` по умолчанию приходит из Router-каталога. Если админ задал override через `PUT /v1/admin/model-display-overrides/{modelId...}`, сервер подменяет только `name` в `GET /v1/api/models`; `id` остаётся техническим canonical `vendor/name` и используется для allow-list, recommendations и биллинга.

**Фильтрация:**
- Состав и порядок каталога определяет Router (выдача consumer_access). Серверное курирование «последние 2 версии на семейство» удалено в v0.33.0.
- **Per-product allow-list (F-000015):** если запрос несёт `X-Product-Key` (и **не** admin-identity) и у продукта настроен allow-list — возвращаются только разрешённые модели, **в admin-заданном порядке** (`position`) (ключ пересечения — `id`). Admin-identity (`swan_ak_*`) → полный каталог без фильтра (для админ-UI чекбоксов, даже если запрос несёт и `X-Product-Key`). Пустой allow-list = все модели. Сбой загрузки списка → `500 MODEL_ACCESS_CHECK_FAILED` (fail-closed). Управление списком — `GET`/`PUT /v1/admin/products/{id}/models`.

---

## GET /v1/models/catalog

**Публичный** каталог моделей с коин-ценами (v0.40.0, F-000022). **Без аутентификации** —
для sales-лендинга до регистрации (S1). Rate-limit per-IP (120 запросов / 5 мин).
Невалидный/отсутствующий ключ игнорируется — эндпоинт всегда truly public.

Путь намеренно вне `/v1/api/*` (auth'd-namespace): это публичная поверхность.

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "provider": "LenSA",
  "models": [
    {
      "id": "openai/gpt-4o-mini",
      "name": "GPT-4o Mini",
      "subProvider": "openai",
      "contextLength": 128000,
      "promptPriceCoins": 0.00003,
      "completionPriceCoins": 0.00012,
      "promptPriceCoinsPer1M": 30,
      "completionPriceCoinsPer1M": 120
    }
  ]
}
```

**Цены (только коины):**
- Отдаются **исключительно коины** (per-token + per-1M). USD/закупка наружу **не выводятся**.
- Фолбэк-цепочка коин-цены (§3): ручной коин-override (`model_prices`) → дневной каталог **Router-sell** (что платим Router'у) × курс `pricing.coins_per_usd` × наценка `pricing.markup` (T-000210) → нули (модель видима, бесплатна). Ручной override наценку НЕ применяет (= final). Закупку Router'а (COGS) сервер захватывает в `usage_stats` для админ-маржи, но клиенту не отдаёт.
- До поставки Router'ом **продажных (sell)** цен в consumer-каталог (REQ-002) авто-ветка пуста → каталог показывает ручной прайс; модели без него — нули.

**Errors:**
- `AI_NOT_CONFIGURED` (503) — Router не сконфигурирован (реестр пуст).
- `MODELS_FETCH_FAILED` (502) — каталог Router'а недоступен.

---

## GET /v1/api/balance  (deprecated-форма: `GET /v1/api/balance/{provider}`)

Канон — `GET /v1/api/balance` **без сегмента** (новый маршрут T-000329, v0.83.0).
Форма `/{provider}` — deprecated-толерантная: значение сегмента игнорируется
(с v0.40.0). Остаток **закупочного** баланса консумера у Router'а (агрегат
`GET /v1/balance` Swan_AiRouter — сумма по всем provider_account'ам). Семантика та же,
что была у OpenRouter credits: «сколько закупки осталось», НЕ баланс пользователя
(тот — `GET /v1/api/me`).

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "provider": "LenSA",
  "balance": 12.50,
  "currency": "USD",
  "asOf": "2026-06-11T10:00:00Z"
}
```

`asOf` — свежесть агрегата (RFC3339, omitempty — отсутствует до первого тика
balance-poller'а Router'а). Ответ `501 BALANCE_NOT_SUPPORTED` удалён в v0.33.0
(единственный провайдер баланс поддерживает).

---

## GET /v1/api/recommendations/{language}

Рекомендованные модели для языка/фреймворка. Возвращает курируемый список моделей, отсортированный по приоритету.

**Параметры пути:**

| Параметр | Тип | Описание |
|----------|-----|----------|
| language | string | Язык: `1c`, `go`, `python`, `javascript`, `flutter`, `csharp`, `java`, `kotlin`, `swift`, `rust`, `php`, `ruby`, `typescript`, `cpp` |

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "language": "1c",
  "recommendations": [
    {
      "modelId": "anthropic/claude-opus-4.6",
      "modelName": "Claude Opus 4.6",
      "reason": "Лучшее качество кода для 1С, отличная поддержка кириллицы"
    }
  ]
}
```

Если язык не найден — 200 с пустым массивом `recommendations: []`.

**Поля `recommendations[]`:**

| Поле | Тип | Описание |
|------|-----|----------|
| modelId | string | ID модели (как в GET /v1/api/models) |
| modelName | string | Человекочитаемое имя |
| reason | string | Обоснование рекомендации (omitempty) |

> Поле `provider` снято в v0.40.0 (T-000148): после cutover провайдер один, рекомендация атрибутируется только моделью.

---

