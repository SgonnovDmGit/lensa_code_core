# Client API — Chat tool-цикл (tool-retry / tool-results)

> Часть client-контракта. Индекс: [client-api.md](client-api.md). Первичный ход — [client-api-chat.md](client-api-chat.md).

**Транспортный кап (T-000338, `v0.85.0`):** тело всех трёх ручек семейства (`/chat/tool-retry`, `/chat/tool-results[/async]`) ограничено **4 MB** (против дефолтных 256 KB прочих JSON-ручек — результаты клиентских тулов `dataB64`/`contextB64`/`queryText` легитимно крупные). Превышение — `413 REQUEST_TOO_LARGE`. Содержимое результатов не усекается: рендер в контекст модели ограничивают существующие серверные капы вывода.

## POST /v1/api/chat/tool-retry

Повторная попытка AI после неудачного применения инструментов на стороне клиента. Клиент вызывает этот эндпоинт, если `apply_code_change` или `execute_command` не удалось выполнить: SEARCH не найден в файле, билд упал после правки, команда завершилась с ошибкой.

Сервер добавляет скрытое `tool_result` сообщение в сессию, повторно вызывает AI с полным контекстом и возвращает новый ответ.

**Request:**
```json
{
  "sessionId": "a1b2c3d4-...",
  "failures": [
    {
      "toolUseId": "tu_b3f4a12e5c6d",
      "failureType": "search_not_found",
      "message": "Фрагмент search не найден в файле main.go",
      "contextB64": "cGFja2FnZSBtYWluCgpmdW5jIG1haW4oKSB7fQo="
    }
  ]
}
```

| Поле | Тип | Обязательно | Описание |
|------|-----|-------------|----------|
| sessionId | string | да | UUID сессии из предыдущего ответа |
| failures | array | да | Минимум один элемент |
| assistMode | string | нет | Режим работы AI (F-000041, REQ-022): как у `/chat` — per-turn, продолжение хода не наследует |
| clientTools | string[] | нет | Negotiation client-executed тулов (F-000042, REQ-057, v0.80.0): capability ЭТОГО запроса, продолжение не наследует. Семантика — как у `/chat`; подробно: [client-api-toolsurface.md](client-api-toolsurface.md), секция «Negotiation clientTools» |

**Поля `failures[]`:**

| Поле | Тип | Обязательно | Описание |
|------|-----|-------------|----------|
| toolUseId | string | да | `toolUseId` неудавшегося вызова из предыдущего ответа |
| failureType | string | да | `search_not_found` \| `file_not_found` \| `build_failed` \| `command_failed` \| `internal_client_error` (v0.63.2, T-000246/REQ-045 — внутренняя ошибка клиента: тул не удалось ВЫЗВАТЬ; НЕ AI-fixable) |
| message | string | да | Краткое описание проблемы |
| contextB64 | string | нет | base64 с произвольным контекстом: содержимое файла, вывод линтера, stderr команды |

**Response 200:**
```json
{
  "serviceInfo": {"success": true},
  "sessionId": "a1b2c3d4-...",
  "content": "Попробую по-другому...",
  "model": "anthropic/claude-sonnet-4",
  "modelDisplayName": "Claude Sonnet 4",
  "codeChanges": [...],
  "executeCommands": [...],
  "toolUses": [],
  "attemptsUsed": 1,
  "attemptsMax": 2,
  "balanceCoins": 19850.5,
  "costCoins": 12.5
}
```

Поле `modelDisplayName` — человекочитаемое имя модели для UI (см. описание в `POST /v1/api/chat`); может отсутствовать (`omitempty`).

Поля `balanceCoins` / `costCoins` — опциональны, тот же контракт присутствия, что в `POST /v1/api/chat` (присутствуют ⟺ commit прошёл). В ветке «лимит retry исчерпан» (ниже) списания нет → поля отсутствуют.

Поле `toolUses` — generic-канал tool_use'ов (см. `Tool surface` секции ниже). Для default-продукта v0.23.0+ содержит `oneC_*` тулы; для query_console_1c — все console-тулы.

**Если лимит retry исчерпан** (`attemptsUsed >= attemptsMax`):
```json
{
  "serviceInfo": {"success": true},
  "sessionId": "a1b2c3d4-...",
  "content": "Лимит автоматических повторных попыток исчерпан.",
  "retryExhausted": true,
  "attemptsUsed": 2,
  "attemptsMax": 2
}
```

Сервер больше не обращается к AI — клиент должен показать пользователю накопленные ошибки.

**Лимит `attemptsMax`** настраивается runtime через `PUT /v1/admin/config` по ключу `ai_tool_retry.max_attempts` (дефолт `5` с `v0.59.1`). **Считаются только ПОДРЯД идущие фейлы** (`v0.59.1`): `attemptsUsed` = число консекутивных tool-раундов с ошибкой в хвосте истории. Успешный tool-раунд (модель добилась прогресса) и новое пользовательское сообщение **сбрасывают счётчик** — успешные агентские раунды и server-inline результаты лимит не жгут. До `v0.59.1` считались все tool_result без разбора, из-за чего длинная агентская сессия упиралась в лимит на первом же реальном фейле.

---

## POST /v1/api/chat/tool-results

Полноценный endpoint для результатов выполнения инструментов — принимает и success, и failure.

Опц. поле `queryText` (string) — как в `POST /v1/api/chat`: текущий нормализованный текст запроса для пассивного версионирования (F-000014). На tool-results изменение текста атрибутируется `author=user`; при наличии в `results[]` apply-результата с `applied` пишется server-версия (агентская правка) + резолв accepted. **Present-vs-absent (T-000244, v0.61.1):** ключ отсутствует → no-op; присутствует, включая `""` → авторитетная актуализация (client-версия «пустой запрос» тоже пишется). Опц. `queryValidation` — как в `POST /v1/api/chat` (T-000245). Опц. `clientTools` (string[]) — negotiation client-executed тулов этого запроса (F-000042, REQ-057, v0.80.0): продолжение хода не наследует снапшот, шлите поле в каждом запросе; семантика — как у `/chat` (см. [client-api-toolsurface.md](client-api-toolsurface.md), секция «Negotiation clientTools»).

**Request:**
```json
{
  "sessionId": "a1b2c3d4-...",
  "results": [
    {
      "toolUseId": "tu_abc123",
      "status": "success",
      "message": "Applied to main.go"
    },
    {
      "toolUseId": "tu_def456",
      "status": "failure",
      "failureType": "command_failed",
      "message": "Command exited with code 1",
      "contextB64": "base64(stderr)"
    }
  ]
}
```

**Поля `results[]`:**

| Поле | Тип | Описание |
|------|-----|----------|
| toolUseId | string | ID вызова инструмента |
| status | string | `"success"` или `"failure"` |
| failureType | string | Тип ошибки: legacy — `search_not_found`, `file_not_found`, `build_failed`, `command_failed`; **`internal_client_error` (v0.63.2, T-000246/REQ-045, оба пути legacy/generic)** — внутренняя ошибка клиента (тул не удалось вызвать: исключение рантайма, обрыв HTTP, ошибка декодирования). НЕ AI-fixable: сервер персистит пару tool_use↔result с маркером `INTERNAL_CLIENT_ERROR`, НЕ пере-гоняет ход через AI и отвечает terminal (`internalError: true` в ответе, см. ниже); retry-cap не тратится (следующее сообщение пользователя сбрасывает счётчик) |
| message | string | Описание результата (legacy) |
| contextB64 | string | Base64 контекст для legacy failure (stderr, содержимое файла) |
| name | string | (generic) Имя тула. Если присутствует — сервер выбирает product-specific unpacker для разворачивания `dataB64`. Если отсутствует — legacy путь (`failureType`/`message`/`contextB64`). **v0.23.0:** обязателен для `oneC_*` тулов default-продукта; рекомендуется для всех тулов (см. ANNOUNCE-003). |
| dataB64 | string | (generic) Base64-JSON payload результата. Формат payload'а зависит от тула — см. секции `Tool surface for query_console_1c` / `Tool surface for default (oneC)` ниже |

**Dispatch (server-side):** наличие непустого `name` переключает обработку результата на generic-путь. Иначе используется legacy-логика swan_code_client. Mixed-product сессии исключены by design — `name` валидируется против tool-set'а продукта сессии; AI, отдавший tool неизвестный для product, получит `UNKNOWN_TOOL` (HTTP 400).

**Валидация `results[]` против последнего assistant_tool_use** (T-000056): сервер сверяет присланные `toolUseId` с tool_use'ами из последнего скрытого `assistant_tool_use` сообщения сессии. Любое нарушение → 400 без обращения к провайдеру.

**Internal-фейл → terminal (v0.63.2, T-000246/REQ-045):** если среди `results[]` есть `failureType: "internal_client_error"`, сервер персистит все результаты (пары закрыты), записывает версии из `queryText` (если прислан), но AI **не вызывает** — ответ `200` в форме retry-ответа с `"internalError": true` и человекочитаемым `content` («ход остановлен, повторите действие»). Клиент показывает пользователю ошибку выполнения, НЕ ответ AI. То же для `/chat/tool-results/async` (terminal 200 вместо 202 — консольная обработка 200-на-продолжении, T-000003).

**Receipt-only → terminal (v0.75.0, T-000296/REQ-055):** опц. поле запроса `receiptOnly: true` — терминальная фиксация **успешного** `apply_query_change` без продолжения хода. Сервер валидирует и персистит пару tool_use↔result, записывает/резолвит query-версии из `queryText` ровно как обычный путь (server-версия + `accepted=true`), но **не резервирует баланс и не вызывает AI** — ответ `200` в форме retry-ответа с `"receipt": true` и `content` («результат применения зафиксирован»). Применяйте только к успешному валидному apply: режим валиден лишь когда `results[]` содержит успешный `apply_query_change` и нет failure-результатов, иначе `400 RECEIPT_ONLY_NOT_APPLICABLE` — failed/invalid apply отправляйте обычным путём (AI-retry цикл). На `/chat/tool-results/async` — тот же синхронный terminal `200` (тикет не создаётся: продолжения нет). После receipt-only компактизация сессии не блокируется буфером (`COMPACTION_BUFFER_PENDING` уходит вместе с pending-версией). Рекомендуется присылать `queryText` — без него pending-версия не резолвится.

**Errors:**
- `NO_PENDING_TOOL_USES` (400) — в сессии нет ожидающих результата tool_use (последнее ассистент-сообщение не содержит tool_use). Возможные причины: AI завершил диалог текстом, клиент уже подтвердил все tool_use, неправильный sessionId.
- `UNKNOWN_TOOL_USE_ID` (400) — присланный `toolUseId` не найден в последнем assistant_tool_use. Сообщение содержит offending id.
- `DUPLICATE_TOOL_RESULT` (400) — один `toolUseId` встречается в `results[]` более одного раза. Сообщение содержит дублирующийся id.
- `MISSING_TOOL_RESULT` (400) — последний assistant_tool_use содержит tool_use, для которых нет результата в `results[]`. Сообщение перечисляет недостающие id.
- `UNKNOWN_TOOL` (400) — для generic-пути: `name` не входит в tool-set продукта сессии.
- `INVALID_TOOL_DATA` (400) — для generic-пути: `dataB64` невозможно распаковать в payload, ожидаемый product-specific unpacker'ом.
- `RECEIPT_ONLY_NOT_APPLICABLE` (400) — `receiptOnly: true` при наборе результатов без успешного `apply_query_change` либо с failure-результатами (T-000296). Ничего не персистится — повторите запрос без флага.
- `SESSION_NOT_FOUND` (404) — `sessionId` не существует в in-memory store.
- `INSUFFICIENT_BALANCE` (402) — не хватает баланса под worst-case стоимость очередного AI-хопа.
- `BILLING_NOT_CONFIGURED` (503) — Swan_Billing не сконфигурён или недоступен (hard-dependency, F-000012). AI не вызывается, резерв не делается.
- `MODEL_NOT_ALLOWED` (403) — модель сессии не входит в allow-list продукта (F-000015). Проверка ДО любых side-effect'ов.
- `MODEL_ACCESS_CHECK_FAILED` (500) — сбой загрузки allow-list (fail-closed).
- `AI_REQUEST_FAILED` (502) — провайдер вернул ошибку.
- `AI_EMPTY_RESPONSE` (502) — AI вернул пустой completed-ответ (ни текста, ни тулов, ни reasoning): апстрим-глюк, за вызов не списано, безопасно повторить (REQ-058, v0.82.0). Пара tool_use↔result при этом уже персистнута (пред-AI этап) — повторный AI-вызов на той же истории делается через `POST /v1/api/chat/tool-retry`. В async-варианте приходит терминалом `status=failed` с этим кодом.

**Response 200:** тот же формат что у `tool-retry`.

---

## POST /v1/api/chat/tool-results/async

Неблокирующий async-вариант `POST /v1/api/chat/tool-results` (REQ-023 Lensa_Query, v0.55.0) — продолжение многораундовой цепочки без заморозки клиента. Симметричен паре `POST /v1/api/chat/async` + `GET /v1/api/chat/{ticketId}/result`, но для отправки результатов тулов, а не первого сообщения.

**Request:** то же тело, что у синхронного `/chat/tool-results` (`sessionId`, `results[]`, опц. `queryText`), плюс опциональное `asyncWaitMs`.

| Поле | Тип | Обязательно | Описание |
|------|-----|-------------|----------|
| sessionId | string | да | UUID существующей сессии (продолжение хода — автосоздание не поддерживается) |
| results | array | да | Результаты тулов — формат идентичен `/chat/tool-results` |
| queryText | string | нет | C3-актуализация текста запроса на этом раунде (как у `/chat/tool-results`, вкл. present-vs-absent семантику T-000244) |
| queryValidation | object | нет | Валидность queryText (T-000245, как у `/chat`) |
| receiptOnly | bool | нет | Терминальная фиксация успешного apply без AI-вызова (T-000296/REQ-055) — синхронный `200` с `receipt: true`, тикет не создаётся; семантика и ограничения как у `/chat/tool-results` |
| asyncWaitMs | int | нет | Принимается для симметрии с `/chat/async` и валидируется на диапазон `[100, 30000]` (`400 INVALID_ASYNC_WAIT` вне диапазона), но **на поведение не влияет** — см. ниже |
| assistMode | string | нет | Режим работы AI (F-000041, REQ-022, v0.77.0): `planning`\|`auto`\|`review` — серверный гейт tool-surface + prompt-пресет; отсутствие поля = `auto`, невалидное значение = `auto` + warn (не 400). Действует только для `swan_code_client`; per-turn — шлите в каждом запросе хода. Подробно: [client-api-toolsurface.md](client-api-toolsurface.md), секция «Режимы работы» |
| clientTools | string[] | нет | Negotiation client-executed тулов (F-000042, REQ-057, v0.80.0): capability ЭТОГО запроса — продолжение хода не наследует снапшот, шлите поле в каждом запросе; на `/tool-results/async` устанавливает снапшот НОВОГО тикета. Семантика — как у `/chat` (отсутствие = полный каталог; `[]` = только server-executed). Подробно: [client-api-toolsurface.md](client-api-toolsurface.md), секция «Negotiation clientTools» |

**Поведение — всегда `202`.** В отличие от `/chat/async` (где быстрый ответ за `asyncWaitMs` отдаётся синхронно `200`), на продолжении цепочки сервер **всегда** возвращает `202` + `ticketId` (fast-path `200` не держится: на раунде результатов тулов модель ещё «думает» над ними, окно почти никогда не успевает). Клиент **всегда** продолжает через поллинг.

**Response 202:**
```json
{
  "serviceInfo": {"success": true},
  "ticketId": "2df9c7fc-19e8-4c99-9a83-bb2185fcff31",
  "status": "pending",
  "sessionId": "a1b2c3d4-..."
}
```

**Поллинг** — тот же `GET /v1/api/chat/{ticketId}/result` (нового эндпоинта поллинга нет). При `completed` — `result` = обычный `ChatResponse` следующего хода (с `toolUses` или финальный без них). Серверный tool-loop (server-only re-submit / mixed, C3 §3G) доводится в фоне, как у `/chat/async`.

**Errors:** preflight-ошибки (валидация `results[]` против незакрытых tool_use, `NO_PENDING_TOOL_USES`, `UNKNOWN_TOOL`, `INVALID_TOOL_DATA`, `SESSION_NOT_FOUND`, billing, `MODEL_NOT_ALLOWED`, `INVALID_ASYNC_WAIT`) возвращаются **синхронно**, как у `/chat/tool-results`. Исключение: исчерпание retry-лимита (только failure-результаты сверх `ai_tool_retry.max_attempts`) отдаётся синхронным `200` с `retryExhausted:true` — AI-продолжения нет, тикет не создаётся.

---

