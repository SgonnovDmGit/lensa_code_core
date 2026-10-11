using System;
using System.Text.Json.Serialization;
using LenSa.Core.Chat.Models;

namespace LenSa.Core.Services.Api
{
    public class ServiceInfo
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("errorCode")]
        public string? ErrorCode { get; set; }
    }

    /// <summary>
    /// Входные лимиты chat-семейства (ANNOUNCE-002, сервер v0.85.0).
    ///
    /// ВАЖНО: это НЕ истина, а подсказка. Настоящий лимит живёт на сервере
    /// (админ-настройка chat.max_message_chars с hot-reload) и может разойтись с нашим
    /// числом в любой момент. Ручки «спросить лимит» в контракте нет — сервер сообщает
    /// его только в ответе на превышение, текстом. Поэтому:
    ///   • это значение служит ТОЛЬКО чтобы предупредить пользователя ДО отправки;
    ///   • при 400 MESSAGE_TOO_LONG показываем СЕРВЕРНЫЙ текст с его цифрой, а не свою.
    /// </summary>
    public static class ChatLimits
    {
        /// <summary>Дефолт сервера на длину message. Символы, не байты.</summary>
        public const int MaxMessageChars = 64_000;

        /// <summary>С этой длины показываем счётчик — 90% лимита.</summary>
        public const int CounterVisibleFrom = (int)(MaxMessageChars * 0.9);
    }

    public class ChatRequest
    {
        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = string.Empty;

        // Провайдер убран из контракта (ANNOUNCE-001): значение сервер игнорирует с v0.40.0,
        // поле опционально с v0.47.0, а GET /v1/api/providers deprecated и отдаёт ["LenSA"].
        // Не шлём лишнего: пустое опускается.
        [JsonPropertyName("provider")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string Provider { get; set; } = string.Empty;

        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("promptCode")]
        public string PromptCode { get; set; } = string.Empty;

        [JsonPropertyName("framework")]
        public string Framework { get; set; } = string.Empty;

        [JsonPropertyName("ide")]
        public string Ide { get; set; } = string.Empty;

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("projectText")]
        public string ProjectText { get; set; } = string.Empty;

        [JsonPropertyName("temperature")]
        public double Temperature { get; set; } = 0.7;

        [JsonPropertyName("thinking")]
        public bool Thinking { get; set; }

        /// <summary>
        /// REQ-007 (v0.59.0): none | minimal | low | medium | high. Приоритетнее thinking.
        /// omitempty → сервер идёт back-compat через thinking. Невалидное значение →
        /// сервер вернёт 400 INVALID_REASONING_EFFORT.
        /// </summary>
        [JsonPropertyName("reasoningEffort")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ReasoningEffort { get; set; }

        /// <summary>
        /// ANNOUNCE-007 (server v0.51.0): только для POST /v1/api/chat/async — сколько ms
        /// сервер держит submit ждущим быстрого ответа. Default 2500, диапазон [100, 30000].
        /// omitempty → сервер применит собственный default. На /v1/api/chat молча игнорируется.
        /// </summary>
        [JsonPropertyName("asyncWaitMs")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? AsyncWaitMs { get; set; }

        /// <summary>
        /// REQ-022 (server v0.77.0), T-000095: planning | auto | review. Сервер режет surface
        /// ДО вызова провайдера — в review модели физически не даётся право писать в живой
        /// модуль (apply_oneC_edit сужается до target: sandbox). Гейтинг делает сервер, клиент
        /// только честно сообщает режим.
        ///
        /// omitempty. Сервер старее v0.77.0 просто проигнорирует незнакомое поле — поэтому
        /// capability-флаг не спрашиваем: лишний запрос ради поведения, которое и так деградирует
        /// в нынешнее.
        /// </summary>
        [JsonPropertyName("assistMode")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AssistMode { get; set; }

        /// <summary>
        /// F-000042 / REQ-057 (server v0.80.0), T-000156: canonical-имена client-executed тулов,
        /// которые клиент умеет исполнить. Определения и JSON-схемы остаются серверными — шлём
        /// только имена. Эффективный surface хода = server-executed + пересечение(серверный
        /// каталог продукта, это поле). Расширить surface поле не может — только сузить.
        ///
        /// Ключ ОТСУТСТВУЕТ = fail-open, полный каталог продукта (поведение старых клиентов).
        /// Пустой массив = ноль client-executed тулов. Это РАЗНЫЕ вещи — поэтому null, а не
        /// Array.Empty: случайно отправленный [] обезоружил бы модель полностью.
        /// </summary>
        [JsonPropertyName("clientTools")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string[]? ClientTools { get; set; }
    }

    // --- Async chat (ANNOUNCE-007 / server v0.51.0) ---

    public static class AsyncChatStatus
    {
        public const string Pending = "pending";
        public const string Completed = "completed";
        public const string Failed = "failed";
        public const string Abandoned = "abandoned";
        public const string Canceled = "canceled";
    }

    /// <summary>
    /// HTTP 202 envelope от POST /v1/api/chat/async, когда AI не успел ответить за asyncWaitMs.
    /// Клиент читает ticketId и переходит к GET /v1/api/chat/{ticketId}/result.
    /// </summary>
    public class AsyncChatSubmitResponse
    {
        [JsonPropertyName("serviceInfo")]
        public ServiceInfo ServiceInfo { get; set; } = new();

        [JsonPropertyName("ticketId")]
        public string TicketId { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = string.Empty;

        [JsonPropertyName("userMessageId")]
        public int? UserMessageId { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }
    }

    /// <summary>
    /// Ответ GET /v1/api/chat/{ticketId}/result. Разные статусы дают разный набор полей:
    /// pending → только ticketId/status/sessionId; completed → result (ChatResponse);
    /// failed/abandoned → errorCode/errorMessage (serviceInfo.success=false, но HTTP всё ещё 200).
    /// </summary>
    public class AsyncChatPollResponse
    {
        [JsonPropertyName("serviceInfo")]
        public ServiceInfo ServiceInfo { get; set; } = new();

        [JsonPropertyName("ticketId")]
        public string TicketId { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = string.Empty;

        [JsonPropertyName("result")]
        public ChatResponse? Result { get; set; }

        [JsonPropertyName("errorCode")]
        public string? ErrorCode { get; set; }

        [JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; set; }
    }

    public class ChatResponse
    {
        [JsonPropertyName("serviceInfo")]
        public ServiceInfo ServiceInfo { get; set; } = new();

        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        /// <summary>
        /// Человекочитаемое имя модели для UI («Claude Opus 4.6», «GPT-5»). Сервер шлёт omitempty.
        /// </summary>
        [JsonPropertyName("modelDisplayName")]
        public string ModelDisplayName { get; set; } = string.Empty;

        [JsonPropertyName("promptTokens")]
        public int PromptTokens { get; set; }

        [JsonPropertyName("completionTokens")]
        public int CompletionTokens { get; set; }

        [JsonPropertyName("totalTokens")]
        public int TotalTokens { get; set; }

        [JsonPropertyName("codeChanges")]
        public CodeChangeDto[]? CodeChanges { get; set; }

        [JsonPropertyName("executeCommands")]
        public ExecuteCommandDto[]? ExecuteCommands { get; set; }

        // v0.23.0+ generic tool_use channel (ANNOUNCE-001). Для default продукта
        // заполняется для onec_* тулов; для query_console_1c — для всех console-тулов.
        [JsonPropertyName("toolUses")]
        public ToolUseDTO[]? ToolUses { get; set; }

        // v0.59.0 reasoning (REQ-007/008). Present ⟺ в ходе реально были размышления.
        [JsonPropertyName("reasoningText")]
        public string? ReasoningText { get; set; }

        [JsonPropertyName("reasoningEffort")]
        public string? ReasoningEffort { get; set; }

        // T-000106 billing. Present ⟺ commit прошёл. Отсутствие ≠ «0».
        [JsonPropertyName("balanceCoins")]
        public decimal? BalanceCoins { get; set; }

        [JsonPropertyName("costCoins")]
        public decimal? CostCoins { get; set; }

        [JsonPropertyName("costUsd")]
        public decimal? CostUsd { get; set; }

        [JsonPropertyName("costRub")]
        public decimal? CostRub { get; set; }

        // T-000107 SERIAL id user-message на сервере — стабильный якорь для retry/attribution.
        [JsonPropertyName("userMessageId")]
        public int? UserMessageId { get; set; }

        // REQ-021 (Lensa_Query, опционально): заголовок сессии.
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        /// <summary>
        /// Занятость контекста ПОСЛЕ хода (REQ-028 / сервер v0.75.0+). Отдельно от биллинговых
        /// счётчиков: promptTokens — трата хода, contextFill — сколько всего набралось.
        /// omitempty: нет объекта — нет данных (не ноль).
        /// </summary>
        [JsonPropertyName("contextFill")]
        public ContextFillDto? ContextFill { get; set; }

        [JsonPropertyName("manualCompaction")]
        public ManualCompactionDto? ManualCompaction { get; set; }

        /// <summary>
        /// Prompt-токены хода, прочитанные из кэша (REQ-027, сервер v0.80.0). ⊆ promptTokens.
        /// omitempty и присутствует ⟺ > 0: у провайдера с непрозрачным маршрутом «ноль» и
        /// «нет данных» в usage неразличимы, поэтому ноль сервер не отдаёт вовсе.
        /// </summary>
        [JsonPropertyName("cachedTokens")]
        public int? CachedTokens { get; set; }

    }

    /// <summary>
    /// Занятость контекстного окна (REQ-053 Lensa_Query, v0.75.0; degraded-форма — REQ-028, v0.80.0).
    ///
    /// Кольцо рисуется от <see cref="CompactionThresholdTokens"/>, а НЕ от окна модели: сервер
    /// компактит РАНЬШЕ потолка окна, и кольцо от contextLength будет врать (прямое указание
    /// сервера в receipt REQ-028). Окно каталогу может быть вообще неизвестно — тогда приходит
    /// degraded-форма (model + lastPromptTokens + compactionThresholdTokens, без window/ratio),
    /// и кольцо всё равно работает. На проде 13.07 именно так: contextLength у всех моделей 0.
    /// </summary>
    public class ContextFillDto
    {
        [JsonPropertyName("model")]
        public string? Model { get; set; }

        /// <summary>Полный отправленный контекст финального AI-вызова: промпт + тулы + история.</summary>
        [JsonPropertyName("lastPromptTokens")]
        public int? LastPromptTokens { get; set; }

        [JsonPropertyName("windowTokens")]
        public int? WindowTokens { get; set; }

        [JsonPropertyName("windowTokensSource")]
        public string? WindowTokensSource { get; set; }

        /// <summary>Окно модели из каталога. Может отсутствовать — каталогу оно неизвестно.</summary>
        [JsonPropertyName("contextWindow")]
        public int? ContextWindow { get; set; }

        /// <summary>Фактическая точка срабатывания авто-сжатия. Отсутствует — сжатие у продукта выключено.</summary>
        [JsonPropertyName("compactionThresholdTokens")]
        public int? CompactionThresholdTokens { get; set; }

        [JsonPropertyName("fillRatio")]
        public double? FillRatio { get; set; }

        [JsonPropertyName("thresholdRatio")]
        public double? ThresholdRatio { get; set; }

        /// <summary>
        /// Заполнение 0..1 для кольца: от порога сжатия, если он известен, иначе от окна модели.
        /// Больше единицы не бывает — сервер честно предупреждает, что клампить обязан клиент.
        /// </summary>
        public double Fill
        {
            get
            {
                var limit = CompactionThresholdTokens is > 0
                    ? CompactionThresholdTokens.Value
                    : ContextWindow ?? 0;
                var numerator = WindowTokens ?? LastPromptTokens ?? 0;
                if (limit <= 0 || numerator <= 0) return 0;
                var f = (double)numerator / limit;
                return f > 1 ? 1 : f;
            }
        }

        /// <summary>Есть что рисовать: известен и расход, и предел.</summary>
        public bool HasData => Fill > 0;
    }

    /// <summary>Server snapshot of whether manual compaction is currently allowed (REQ-085).</summary>
    public class ManualCompactionDto
    {
        [JsonPropertyName("canCompact")]
        public bool CanCompact { get; set; }

        [JsonPropertyName("reasonCode")]
        public string? ReasonCode { get; set; }

        [JsonPropertyName("projectedTokens")]
        public int? ProjectedTokens { get; set; }

        [JsonPropertyName("floorTokens")]
        public int? FloorTokens { get; set; }
    }

    public class CodeChangeDto
    {
        [JsonPropertyName("file")]
        public string File { get; set; } = string.Empty;

        [JsonPropertyName("operation")]
        public string Operation { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("searchB64")]
        public string SearchB64 { get; set; } = string.Empty;

        [JsonPropertyName("replaceB64")]
        public string ReplaceB64 { get; set; } = string.Empty;

        [JsonPropertyName("toolUseId")]
        public string ToolUseId { get; set; } = string.Empty;
    }

    public class ExecuteCommandDto
    {
        [JsonPropertyName("command")]
        public string Command { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("toolUseId")]
        public string ToolUseId { get; set; } = string.Empty;
    }

    public class ModelDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("subProvider")]
        public string SubProvider { get; set; } = string.Empty;

        [JsonPropertyName("contextLength")]
        public int ContextLength { get; set; }

        // Coin-цены (ANNOUNCE-004 v0.33.0 — USD-поля promptPrice/completionPrice/priceOverridden удалены)
        [JsonPropertyName("promptPriceCoins")]
        public decimal PromptPriceCoins { get; set; }

        [JsonPropertyName("completionPriceCoins")]
        public decimal CompletionPriceCoins { get; set; }

        // Per-1M токенов для читаемости (ANNOUNCE-005 v0.40.0)
        [JsonPropertyName("promptPriceCoinsPer1M")]
        public decimal PromptPriceCoinsPer1M { get; set; }

        [JsonPropertyName("completionPriceCoinsPer1M")]
        public decimal CompletionPriceCoinsPer1M { get; set; }

        [JsonIgnore]
        public string DisplayContext => ContextLength > 0
            ? $"{ContextLength / 1000}K"
            : string.Empty;

        [JsonIgnore]
        public string DisplayPrice => PromptPriceCoinsPer1M > 0
            ? $"{PromptPriceCoinsPer1M:F0}/{CompletionPriceCoinsPer1M:F0} coins/1M"
            : string.Empty;

        public override string ToString() => !string.IsNullOrEmpty(Name) ? Name : Id;
    }

    public class ModelsResponse
    {
        [JsonPropertyName("serviceInfo")]
        public ServiceInfo ServiceInfo { get; set; } = new();

        [JsonPropertyName("provider")]
        public string Provider { get; set; } = string.Empty;

        [JsonPropertyName("models")]
        public ModelDto[] Models { get; set; } = Array.Empty<ModelDto>();
    }

    public class ErrorResponse
    {
        [JsonPropertyName("serviceInfo")]
        public ServiceInfo ServiceInfo { get; set; } = new();

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    public static class ToolFailureTypes
    {
        public const string SearchNotFound = "search_not_found";
        public const string FileNotFound = "file_not_found";
        public const string BuildFailed = "build_failed";
        public const string CommandFailed = "command_failed";
    }

    public class ChatToolRetryResponse
    {
        [JsonPropertyName("serviceInfo")]
        public ServiceInfo ServiceInfo { get; set; } = new();

        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        /// <summary>
        /// Человекочитаемое имя модели для UI. Сервер шлёт omitempty.
        /// </summary>
        [JsonPropertyName("modelDisplayName")]
        public string ModelDisplayName { get; set; } = string.Empty;

        [JsonPropertyName("codeChanges")]
        public CodeChangeDto[]? CodeChanges { get; set; }

        [JsonPropertyName("executeCommands")]
        public ExecuteCommandDto[]? ExecuteCommands { get; set; }

        [JsonPropertyName("attemptsUsed")]
        public int AttemptsUsed { get; set; }

        [JsonPropertyName("attemptsMax")]
        public int AttemptsMax { get; set; }

        [JsonPropertyName("retryExhausted")]
        public bool RetryExhausted { get; set; }

        [JsonPropertyName("lastFailure")]
        public ChatToolLastFailure? LastFailure { get; set; }

        [JsonPropertyName("promptTokens")]
        public int PromptTokens { get; set; }

        [JsonPropertyName("completionTokens")]
        public int CompletionTokens { get; set; }

        [JsonPropertyName("totalTokens")]
        public int TotalTokens { get; set; }

        [JsonPropertyName("toolUses")]
        public ToolUseDTO[]? ToolUses { get; set; }

        [JsonPropertyName("reasoningText")]
        public string? ReasoningText { get; set; }

        [JsonPropertyName("reasoningEffort")]
        public string? ReasoningEffort { get; set; }

        [JsonPropertyName("balanceCoins")]
        public decimal? BalanceCoins { get; set; }

        [JsonPropertyName("costCoins")]
        public decimal? CostCoins { get; set; }

        [JsonPropertyName("costUsd")]
        public decimal? CostUsd { get; set; }

        [JsonPropertyName("costRub")]
        public decimal? CostRub { get; set; }

        /// <summary>Занятость контекста после этого раунда (REQ-028). Ход после тула — тот же ход.</summary>
        [JsonPropertyName("contextFill")]
        public ContextFillDto? ContextFill { get; set; }

        [JsonPropertyName("manualCompaction")]
        public ManualCompactionDto? ManualCompaction { get; set; }

        /// <summary>Prompt-токены раунда из кэша (REQ-027). Присутствует ⟺ > 0.</summary>
        [JsonPropertyName("cachedTokens")]
        public int? CachedTokens { get; set; }

    }

    public class ChatToolLastFailure
    {
        [JsonPropertyName("toolName")]
        public string? ToolName { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }

    // --- User DTOs ---

    public class MeResponse
    {
        [JsonPropertyName("serviceInfo")]
        public ServiceInfo ServiceInfo { get; set; } = new();

        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("infraconnectId")]
        public string InfraConnectId { get; set; } = string.Empty;

        [JsonPropertyName("balanceCoins")]
        public decimal BalanceCoins { get; set; }

        [JsonPropertyName("balanceReservedCoins")]
        public decimal BalanceReservedCoins { get; set; }
    }

    public class UsageEntry
    {
        // provider убран с v0.40.0 (ANNOUNCE-005) — провайдер канонический всегда

        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("promptTokens")]
        public int PromptTokens { get; set; }

        [JsonPropertyName("completionTokens")]
        public int CompletionTokens { get; set; }

        [JsonPropertyName("totalTokens")]
        public int TotalTokens { get; set; }

        [JsonPropertyName("costCoins")]
        public decimal CostCoins { get; set; }

        [JsonPropertyName("createdAt")]
        public string CreatedAt { get; set; } = string.Empty;
    }

    public class UsageResponse
    {
        [JsonPropertyName("serviceInfo")]
        public ServiceInfo ServiceInfo { get; set; } = new();

        [JsonPropertyName("usage")]
        public UsageEntry[] Usage { get; set; } = Array.Empty<UsageEntry>();

        [JsonPropertyName("total")]
        public int Total { get; set; }

        [JsonPropertyName("page")]
        public int Page { get; set; }

        [JsonPropertyName("size")]
        public int Size { get; set; }
    }

    // --- Recommendations (REQ-008) ---

    public class RecommendationDto
    {
        // provider убран с v0.40.0 (ANNOUNCE-005) — провайдер канонический всегда

        [JsonPropertyName("modelId")]
        public string ModelId { get; set; } = string.Empty;

        [JsonPropertyName("modelName")]
        public string ModelName { get; set; } = string.Empty;

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = string.Empty;
    }

    public class RecommendationsResponse
    {
        [JsonPropertyName("serviceInfo")]
        public ServiceInfo ServiceInfo { get; set; } = new();

        [JsonPropertyName("language")]
        public string Language { get; set; } = string.Empty;

        [JsonPropertyName("recommendations")]
        public RecommendationDto[] Recommendations { get; set; } = Array.Empty<RecommendationDto>();
    }

    // --- Tool Results (REQ-009) ---

    public static class ToolResultStatus
    {
        public const string Success = "success";
        public const string Failure = "failure";
    }

    public class ToolResult
    {
        [JsonPropertyName("toolUseId")]
        public string ToolUseId { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("failureType")]
        public string? FailureType { get; set; }

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("contextB64")]
        public string? ContextB64 { get; set; }
    }

    public class ChatToolResultsRequest
    {
        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = string.Empty;

        [JsonPropertyName("results")]
        public ToolResult[] Results { get; set; } = Array.Empty<ToolResult>();
    }

    /// <summary>
    /// Новая обёртка под ApiClient.PostToolResultsAsync — с ToolResultItem envelope
    /// (LenSa.Core.Chat.Models). Используется ChatViewModelBase. Старый
    /// ChatToolResultsRequest с ToolResult остаётся для 1С AiViewModel до T-000048.
    /// </summary>
    public class ChatToolResultsEnvelope
    {
        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = string.Empty;

        [JsonPropertyName("results")]
        public ToolResultItem[] Results { get; set; } = Array.Empty<ToolResultItem>();

        /// <summary>
        /// T-000095. Режим обязан ехать и в ПРОДОЛЖЕНИИ хода, а не только в первом запросе:
        /// правки модель делает именно здесь, в цепочке tool-вызовов. Отправь режим один раз —
        /// и «Ревью» перестанет действовать ровно на том ходу, ради которого он включён.
        /// </summary>
        [JsonPropertyName("assistMode")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AssistMode { get; set; }

        /// <summary>
        /// T-000156. Тоже per-request и тоже без наследования: продолжение хода без поля сервер
        /// считает fail-open и вернёт полный каталог. Побочный эффект контракта, ради которого
        /// он так и сделан: существующую сессию может безопасно резюмировать клиент ДРУГОЙ версии.
        /// </summary>
        [JsonPropertyName("clientTools")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string[]? ClientTools { get; set; }
    }

    // --- История диалогов (T-000106) ---

    /// <summary>
    /// Элемент списка GET /v1/api/sessions: id + title (авто из первого сообщения,
    /// REQ-021) + модель + даты. lastMessageAt — денорм активности; если сообщений
    /// нет, сервер отдаёт createdAt.
    /// </summary>
    public class SessionDto
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("createdAt")]
        public string CreatedAt { get; set; } = string.Empty;

        [JsonPropertyName("lastMessageAt")]
        public string LastMessageAt { get; set; } = string.Empty;

        [JsonPropertyName("lastMessagePreview")]
        public string? LastMessagePreview { get; set; }
    }

    /// <summary>
    /// Ответ GET /v1/api/sessions (cursor-режим, server v0.52.0):
    /// сортировка lastMessageAt DESC, hasMore/nextCursor для подгрузки следующих страниц.
    /// </summary>
    public class SessionsListResponse
    {
        [JsonPropertyName("serviceInfo")]
        public ServiceInfo ServiceInfo { get; set; } = new();

        [JsonPropertyName("sessions")]
        public SessionDto[] Sessions { get; set; } = Array.Empty<SessionDto>();

        [JsonPropertyName("total")]
        public int Total { get; set; }

        [JsonPropertyName("hasMore")]
        public bool HasMore { get; set; }

        [JsonPropertyName("nextCursor")]
        public string? NextCursor { get; set; }

        [JsonPropertyName("limit")]
        public int Limit { get; set; }
    }

    /// <summary>Заголовок/модель диалога в ответе /messages (omitempty на сервере).</summary>
    public class SessionInfoDto
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }
    }

    /// <summary>
    /// Вызов тула в историческом assistant_tool_use сообщении (T-000108):
    /// name + input тула; isError (REQ-027) — исход выполнения, omitempty
    /// (отсутствует у старых записей — клиент читает мягко, рисует успех).
    /// </summary>
    public class SessionToolCallDto
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("input")]
        public System.Text.Json.JsonElement Input { get; set; }

        [JsonPropertyName("isError")]
        public bool? IsError { get; set; }
    }

    /// <summary>
    /// Сообщение из GET /v1/api/sessions/{id}/messages. Присутствие опциональных
    /// полей — по контракту /chat: отсутствие = «нет данных», не «ноль».
    /// reasoningText в контракте истории НЕ отдаётся (в отличие от живого /chat).
    /// </summary>
    public class SessionMessageDto
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("kind")]
        public string Kind { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("createdAt")]
        public string CreatedAt { get; set; } = string.Empty;

        // Attribution хода (v0.50.0, F-000026): requested model / фактическая модель Router.
        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("realModel")]
        public string? RealModel { get; set; }

        [JsonPropertyName("modelDisplayName")]
        public string? ModelDisplayName { get; set; }

        [JsonPropertyName("temperature")]
        public double? Temperature { get; set; }

        [JsonPropertyName("toolCalls")]
        public SessionToolCallDto[]? ToolCalls { get; set; }

        [JsonPropertyName("promptTokens")]
        public int? PromptTokens { get; set; }

        [JsonPropertyName("completionTokens")]
        public int? CompletionTokens { get; set; }

        [JsonPropertyName("totalTokens")]
        public int? TotalTokens { get; set; }

        [JsonPropertyName("costCoins")]
        public decimal? CostCoins { get; set; }

        [JsonPropertyName("costUsd")]
        public decimal? CostUsd { get; set; }

        [JsonPropertyName("costRub")]
        public decimal? CostRub { get; set; }

        [JsonPropertyName("genMs")]
        public long? GenMs { get; set; }

        [JsonPropertyName("tokensPerSec")]
        public double? TokensPerSec { get; set; }

        /// <summary>
        /// Размышления модели по ходу (сервер отдаёт с v0.59.0, включая строки tool_use).
        /// Мы их не рендерили — и получали «голые карточки» вызовов без комментария: reasoning-
        /// модели говорят НЕ в content, а здесь. Это и был настоящий корень REQ-026, который
        /// сервер справедливо отклонил, подняв наши же прод-сессии (T-000160).
        /// </summary>
        [JsonPropertyName("reasoningText")]
        public string? ReasoningText { get; set; }

        /// <summary>
        /// Фактически применённый уровень размышлений (REQ-025, сервер v0.80.0): none|minimal|
        /// low|medium|high. omitempty — строка записана до v0.80.0; читать как «неизвестно» и
        /// падать на дефолт проекта, НЕ на none (прямое указание сервера).
        /// </summary>
        [JsonPropertyName("reasoningEffort")]
        public string? ReasoningEffort { get; set; }

        /// <summary>Prompt-токены хода из кэша (REQ-027). Присутствует ⟺ > 0.</summary>
        [JsonPropertyName("cachedTokens")]
        public int? CachedTokens { get; set; }

        [JsonPropertyName("checkpointTrigger")]
        public string? CheckpointTrigger { get; set; }

        [JsonPropertyName("tokensBefore")]
        public int? TokensBefore { get; set; }

        [JsonPropertyName("windowTokensAfterEstimate")]
        public int? WindowTokensAfterEstimate { get; set; }

        [JsonPropertyName("windowTokensAfterActual")]
        public int? WindowTokensAfterActual { get; set; }

        [JsonPropertyName("checkpointCoversThroughMessageId")]
        public int? CheckpointCoversThroughMessageId { get; set; }
    }

    /// <summary>
    /// Ответ GET /v1/api/sessions/{id}/messages: сообщения по возрастанию id
    /// (хронологически), для подгрузки более старых — before = id верхнего.
    /// </summary>
    public class SessionMessagesResponse
    {
        [JsonPropertyName("serviceInfo")]
        public ServiceInfo ServiceInfo { get; set; } = new();

        [JsonPropertyName("session")]
        public SessionInfoDto? Session { get; set; }

        [JsonPropertyName("messages")]
        public SessionMessageDto[] Messages { get; set; } = Array.Empty<SessionMessageDto>();

        /// <summary>True when messages exist before this page; omitted by older servers.</summary>
        [JsonPropertyName("hasOlder")]
        public bool HasOlder { get; set; }

        /// <summary>
        /// Занятость контекста сессии на момент перезагрузки — **top-level**, а не в сообщениях
        /// (источник: свежайший измеренный ход сессии). Именно здесь я и промахнулся в v0.17.5:
        /// повесил поле на SessionMessageDto, где его не бывает никогда, — и кольцо после
        /// перезагрузки диалога оставалось пустым, хотя на живом ходе работало (T-000161).
        /// </summary>
        [JsonPropertyName("contextFill")]
        public ContextFillDto? ContextFill { get; set; }

        [JsonPropertyName("manualCompaction")]
        public ManualCompactionDto? ManualCompaction { get; set; }
    }

    /// <summary>
    /// Ответ POST /v1/api/sessions/{id}/compact (T-000133): ручное сжатие контекста —
    /// старая история сворачивается в rolling-summary. Неразрушающе: сами сообщения
    /// сервер не трогает, поэтому лента у пользователя остаётся как была.
    /// </summary>
    public class CompactResponse
    {
        [JsonPropertyName("serviceInfo")]
        public ServiceInfo ServiceInfo { get; set; } = new();

        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = string.Empty;

        [JsonPropertyName("summary")]
        public string Summary { get; set; } = string.Empty;

        [JsonPropertyName("referencesCount")]
        public int ReferencesCount { get; set; }

        /// <summary>Модель, которой сжимали (может отличаться от модели диалога).</summary>
        [JsonPropertyName("model")]
        public string? Model { get; set; }

        /// <summary>Занятость контекста на момент запуска. omitempty — «не измерено», не ноль.</summary>
        [JsonPropertyName("tokensBefore")]
        public int? TokensBefore { get; set; }

        /// <summary>Deprecated alias for summary-plus-remainder; this value is not window occupancy.</summary>
        [JsonPropertyName("tokensAfterEstimate")]
        public int? TokensAfterEstimate { get; set; }

        [JsonPropertyName("contextFill")]
        public ContextFillDto? ContextFill { get; set; }

        [JsonPropertyName("manualCompaction")]
        public ManualCompactionDto? ManualCompaction { get; set; }

        [JsonPropertyName("summaryPlusRemainderTokens")]
        public int? SummaryPlusRemainderTokens { get; set; }

        [JsonPropertyName("windowTokensAfterEstimate")]
        public int? WindowTokensAfterEstimate { get; set; }
    }

    public class ApiException : Exception
    {
        public string ErrorCode { get; }

        public ApiException(string errorCode, string message) : base(message)
        {
            ErrorCode = errorCode;
        }
    }

    public sealed class ChatTurnCanceledException : OperationCanceledException
    {
        public ChatTurnCanceledException() : base("The async chat turn was canceled by the server.") { }
    }
}
