using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LenSa.Core.Chat.Models;

namespace LenSa.Core.Services.Api
{
    public class ApiClient
    {
        /// <summary>
        /// Сериализация тела запросов (T-000163).
        ///
        /// **Не убирать.** Дефолтный энкодер System.Text.Json экранирует всё не-ASCII в
        /// `\uXXXX` — это **6 байт на символ кириллицы** вместо двух в UTF-8. Замер:
        /// 64 000 символов кириллицы дают 384 014 байт тела при дефолте и 128 014 с этим
        /// энкодером. Транспортный кап сервера — 262 144 байта, то есть на дефолте в него
        /// влезало лишь ~43 000 символов, и заявленный сервером лимит в 64 000 был
        /// недостижим в принципе: пользователь получал 413 REQUEST_TOO_LARGE на тексте,
        /// который обязан был пройти.
        ///
        /// Берём `Create(UnicodeRanges.All)`, а НЕ `UnsafeRelaxedJsonEscaping`: выигрыш в
        /// размере тот же, но `&lt;`, `&gt;`, `&amp;`, `'` остаются экранированными.
        /// </summary>
        private static readonly JsonSerializerOptions BodyJson = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(
                System.Text.Unicode.UnicodeRanges.All)
        };

        private readonly HttpClient _http;
        private string _baseUrl;

        public string BaseUrl
        {
            get => _baseUrl;
            set
            {
                // localhost / 127.0.0.1 — dev-режим на http; внешние домены — https.
                // Реальные окружения (nginx / openresty) редиректят http → https через 301, а
                // .NET HttpClient стрипает Authorization заголовок на cross-scheme redirect
                // (стандартная защита) — итог: сервер видит запрос без user-key и отвечает
                // USER_REQUIRED. Auto-upgrade убирает redirect и Authorization доходит целым.
                var raw = value.TrimEnd('/');
                var stripped = raw;
                if (stripped.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                    stripped = stripped[7..];
                else if (stripped.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    stripped = stripped[8..];

                var isLocal = stripped.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) ||
                              stripped.StartsWith("127.0.0.1") ||
                              stripped.StartsWith("[::1]");
                _baseUrl = (isLocal ? "http://" : "https://") + stripped;
            }
        }

        public string? ProductKey { get; set; }
        public string? UserKey { get; set; }

        /// <summary>
        /// Диагностический хук сырых тел chat-ответов (T-000112). Клиент подключает
        /// его к файловому логу при windowTrackerLog; null — no-op. Тела режутся по месту.
        /// </summary>
        public static Action<string>? DiagLog;

        private static void Diag(string tag, string body)
        {
            var log = DiagLog;
            if (log == null) return;
            var b = body.Length > 1500 ? body[..1500] + $"…(+{body.Length - 1500})" : body;
            try { log($"{tag}: {b}"); } catch { }
        }

        public ApiClient(string baseUrl)
        {
            _baseUrl = string.Empty;
            BaseUrl = baseUrl;
            // ANNOUNCE-002: server v0.25.0 has WriteTimeout=300s; client buffer above that
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(330) };
        }

        public async Task<bool> CheckHealthAsync()
        {
            try
            {
                using var request = CreateRequest(HttpMethod.Get, "/health");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task<ModelDto[]> GetModelsAsync(string provider)
        {
            // С v0.40.0 (ANNOUNCE-005) path-параметр {provider} игнорируется: провайдер
            // канонический, а с ANNOUNCE-001 понятие провайдера убрано из контракта вовсе.
            // GetProvidersAsync / GetBalanceAsync удалены (T-000068): UI выбора провайдера убран,
            // баланс всегда GetMeAsync().BalanceCoins (внутренние монеты).
            using var request = CreateRequest(HttpMethod.Get, $"/v1/api/models/{Uri.EscapeDataString(provider)}");
            var result = await SendAsync<ModelsResponse>(request);
            return result.Models;
        }

        public async Task<ChatResponse> SendChatAsync(ChatRequest chatRequest)
        {
            using var request = CreateRequest(HttpMethod.Post, "/v1/api/chat");
            var json = JsonSerializer.Serialize(chatRequest, BodyJson);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return await SendAsync<ChatResponse>(request);
        }

        /// <summary>
        /// Async chat submit + автоматический polling результата (ANNOUNCE-007, server v0.51.0).
        /// POST /v1/api/chat/async с asyncWaitMs:
        ///   * если AI успел за окно — сервер отдаёт HTTP 200 с обычным ChatResponse (сразу возвращаем);
        ///   * если не успел — HTTP 202 с ticketId, клиент опрашивает GET /v1/api/chat/{ticketId}/result
        ///     каждые PollIntervalMs, пока не придёт terminal статус (completed/failed/abandoned).
        /// CancellationToken прекращает и submit, и polling (кнопка Interrupt в UI).
        /// </summary>
        public async Task<ChatResponse> SendChatAsyncAsync(
            ChatRequest chatRequest,
            int asyncWaitMs = 2500,
            CancellationToken cancellationToken = default)
        {
            chatRequest.AsyncWaitMs = asyncWaitMs;

            using var request = CreateRequest(HttpMethod.Post, "/v1/api/chat/async");
            var json = JsonSerializer.Serialize(chatRequest, BodyJson);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            HttpResponseMessage submitResponse;
            try
            {
                submitResponse = await _http.SendAsync(request, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                throw new ApiException("CONNECTION_ERROR", ex.Message);
            }
            catch (TaskCanceledException)
            {
                throw new ApiException("TIMEOUT", "Request timed out");
            }

            using (submitResponse)
            {
                var submitBody = await submitResponse.Content.ReadAsStringAsync(cancellationToken);
                Diag($"submit HTTP {(int)submitResponse.StatusCode}", submitBody);

                if (submitResponse.StatusCode == HttpStatusCode.OK)
                    return DeserializeOrThrow<ChatResponse>(submitBody);

                if (submitResponse.StatusCode == HttpStatusCode.Accepted)
                {
                    var submitEnvelope = DeserializeOrThrow<AsyncChatSubmitResponse>(submitBody);
                    if (string.IsNullOrEmpty(submitEnvelope.TicketId))
                        throw new ApiException("PARSE_ERROR", "202 response without ticketId");

                    return await PollChatResultAsync(
                        submitEnvelope.TicketId,
                        submitEnvelope.SessionId,
                        cancellationToken);
                }

                ThrowFromErrorBody(submitResponse, submitBody);
                throw new ApiException("UNKNOWN", "Unreachable"); // сатисфакция компилятора
            }
        }

        /// <summary>
        /// Интервал между опросами GET /v1/api/chat/{ticketId}/result. Компромисс:
        /// 1500 ms — достаточно, чтобы не долбить сервер на длинных ходах (обычно 5-30 сек),
        /// и достаточно быстро для восприятия "живого" ожидания. Сервер не рекомендует
        /// конкретного значения — на его стороне hold на подписке, poll лишь дёшево читает статус.
        /// </summary>
        private const int PollIntervalMs = 1500;

        private async Task<ChatResponse> PollChatResultAsync(
            string ticketId,
            string sessionIdFromSubmit,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await Task.Delay(PollIntervalMs, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }

                using var pollRequest = CreateRequest(
                    HttpMethod.Get,
                    $"/v1/api/chat/{Uri.EscapeDataString(ticketId)}/result");

                HttpResponseMessage pollResponse;
                try
                {
                    pollResponse = await _http.SendAsync(pollRequest, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (HttpRequestException ex)
                {
                    throw new ApiException("CONNECTION_ERROR", ex.Message);
                }
                catch (TaskCanceledException)
                {
                    throw new ApiException("TIMEOUT", "Polling request timed out");
                }

                using (pollResponse)
                {
                    var pollBody = await pollResponse.Content.ReadAsStringAsync(cancellationToken);
                    Diag($"poll HTTP {(int)pollResponse.StatusCode}", pollBody);

                    if (!pollResponse.IsSuccessStatusCode)
                    {
                        ThrowFromErrorBody(pollResponse, pollBody);
                        return null!; // недостижимо
                    }

                    var poll = DeserializeOrThrow<AsyncChatPollResponse>(pollBody);

                    switch (poll.Status)
                    {
                        case AsyncChatStatus.Pending:
                            continue;

                        case AsyncChatStatus.Completed:
                            if (poll.Result == null)
                                throw new ApiException("PARSE_ERROR", "completed result without body");
                            // Сервер может не заполнять sessionId внутри result — подмешиваем из envelope.
                            if (string.IsNullOrEmpty(poll.Result.SessionId))
                                poll.Result.SessionId = !string.IsNullOrEmpty(poll.SessionId)
                                    ? poll.SessionId
                                    : sessionIdFromSubmit;
                            return poll.Result;

                        case AsyncChatStatus.Failed:
                        case AsyncChatStatus.Abandoned:
                            throw new ApiException(
                                poll.ErrorCode ?? poll.ServiceInfo?.ErrorCode ?? "AI_REQUEST_FAILED",
                                poll.ErrorMessage ?? poll.Status);

                        default:
                            throw new ApiException("UNKNOWN_STATUS", $"Unexpected poll status '{poll.Status}'");
                    }
                }
            }
        }

        private static T DeserializeOrThrow<T>(string body)
        {
            var result = JsonSerializer.Deserialize<T>(body);
            if (result == null)
                throw new ApiException("PARSE_ERROR", "Failed to parse response");
            return result;
        }

        private static void ThrowFromErrorBody(HttpResponseMessage response, string body)
        {
            try
            {
                var error = JsonSerializer.Deserialize<ErrorResponse>(body);
                if (error?.ServiceInfo != null)
                    throw new ApiException(
                        error.ServiceInfo.ErrorCode ?? "UNKNOWN",
                        error.Message ?? response.ReasonPhrase ?? "Unknown error");
            }
            catch (JsonException) { }

            throw new ApiException(
                $"HTTP_{(int)response.StatusCode}",
                response.ReasonPhrase ?? "Unknown error");
        }

        public async Task<MeResponse> GetMeAsync()
        {
            using var request = CreateRequest(HttpMethod.Get, "/v1/api/me");
            return await SendAsync<MeResponse>(request);
        }

        public async Task<UsageResponse> GetUsageAsync(int page = 1, int size = 50)
        {
            using var request = CreateRequest(HttpMethod.Get, $"/v1/api/me/usage?page={page}&size={size}");
            return await SendAsync<UsageResponse>(request);
        }

        public async Task<RecommendationDto[]> GetRecommendationsAsync(string language)
        {
            using var request = CreateRequest(HttpMethod.Get, $"/v1/api/recommendations/{Uri.EscapeDataString(language)}");
            try
            {
                var result = await SendAsync<RecommendationsResponse>(request);
                return result.Recommendations;
            }
            catch (ApiException ex) when (ex.ErrorCode == "LANGUAGE_NOT_FOUND")
            {
                return Array.Empty<RecommendationDto>();
            }
        }

        public async Task<ChatToolRetryResponse> SendChatToolResultsAsync(ChatToolResultsRequest resultsRequest)
        {
            using var request = CreateRequest(HttpMethod.Post, "/v1/api/chat/tool-results");
            var json = JsonSerializer.Serialize(resultsRequest, BodyJson);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return await SendAsync<ChatToolRetryResponse>(request);
        }

        /// <summary>
        /// Отправить tool-результаты (unified envelope из LenSa.Core.Chat.Models.ToolResultItem).
        /// Используется ChatViewModelBase после DispatchToolUsesAsync. Ответ — стандартный
        /// ChatToolRetryResponse (следующий assistant-ход с возможными новыми toolUses[]).
        /// Old SendChatToolResultsAsync с legacy ToolResult остаётся для 1С AiViewModel до T-000048.
        /// </summary>
        public async Task<ChatToolRetryResponse> PostToolResultsAsync(
            string sessionId,
            IReadOnlyList<ToolResultItem> results,
            string? assistMode = null,
            string[]? clientTools = null)
        {
            using var request = CreateRequest(HttpMethod.Post, "/v1/api/chat/tool-results");
            var body = new ChatToolResultsEnvelope
            {
                SessionId = sessionId,
                Results = results is ToolResultItem[] arr ? arr : System.Linq.Enumerable.ToArray(results),
                AssistMode = assistMode,
                ClientTools = clientTools
            };
            var json = JsonSerializer.Serialize(body, BodyJson);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return await SendAsync<ChatToolRetryResponse>(request);
        }

        // --- История диалогов (T-000106) ----------------------------------

        /// <summary>
        /// Список диалогов пользователя (GET /v1/api/sessions, cursor-режим v0.52.0):
        /// сортировка по активности (lastMessageAt DESC). cursor — nextCursor из
        /// предыдущей страницы, null — первая страница.
        /// </summary>
        public async Task<SessionsListResponse> GetSessionsAsync(int limit = 50, string? cursor = null)
        {
            var path = $"/v1/api/sessions?limit={limit}";
            if (!string.IsNullOrEmpty(cursor))
                path += $"&cursor={Uri.EscapeDataString(cursor)}";
            using var request = CreateRequest(HttpMethod.Get, path);
            return await SendAsync<SessionsListResponse>(request);
        }

        /// <summary>
        /// История чата диалога (GET /v1/api/sessions/{id}/messages). Сообщения по
        /// возрастанию id; before — id верхнего сообщения текущей страницы для
        /// подгрузки более старых. Чужая/несуществующая сессия → 404 SESSION_NOT_FOUND.
        /// </summary>
        public async Task<SessionMessagesResponse> GetSessionMessagesAsync(
            string sessionId, int limit = 50, int? before = null)
        {
            var path = $"/v1/api/sessions/{Uri.EscapeDataString(sessionId)}/messages?limit={limit}";
            if (before.HasValue)
                path += $"&before={before.Value}";
            using var request = CreateRequest(HttpMethod.Get, path);
            return await SendAsync<SessionMessagesResponse>(request);
        }

        /// <summary>
        /// Ручное сжатие контекста диалога (POST /v1/api/sessions/{id}/compact, T-000133).
        /// Тело опционально (пустое = модель компактизатора по умолчанию) — не шлём.
        /// Сжатие — обычный AI-вызов и списывается с баланса. Отказы, которые надо понимать:
        /// 409 COMPACTION_BELOW_FLOOR (сжимать ещё нечего), 402 INSUFFICIENT_BALANCE,
        /// 503 COMPACTION_NOT_CONFIGURED.
        /// </summary>
        public async Task<CompactResponse> CompactSessionAsync(string sessionId)
        {
            var path = $"/v1/api/sessions/{Uri.EscapeDataString(sessionId)}/compact";
            using var request = CreateRequest(HttpMethod.Post, path);
            return await SendAsync<CompactResponse>(request);
        }

        // SSE-стрим (POST /v1/api/chat/stream) отключён на сервере с ANNOUNCE-004 v0.33.0 —
        // сервер безусловно отвечает 501 STREAMING_NOT_SUPPORTED, пока Router-SSE не вернётся.
        // Клиентский метод SendChatStreamAsync (и модель SseDoneData) удалены — восстановим,
        // когда стрим появится обратно (T-000003).

        private HttpRequestMessage CreateRequest(HttpMethod method, string path)
        {
            var request = new HttpRequestMessage(method, $"{_baseUrl}{path}");
            request.Headers.Add("X-Request-ID", Guid.NewGuid().ToString());

            // Product identity (статический ключ сборки) — на всех /v1/api/* и /v1/feedback и /v1/auth/otp/*
            if (!string.IsNullOrEmpty(ProductKey))
                request.Headers.Add("X-Product-Key", ProductKey);

            // User identity — long-lived bearer-ключ. Не отправляется на OTP-эндпоинтах (юзера ещё нет).
            // Используем TryAddWithoutValidation — HeaderValidation .NET-а может тихо отбросить
            // заголовок для нестандартных user-key (генерация ключа уехала в микросервис,
            // формат теперь произвольный). AuthenticationHeaderValue ctor валидирует token68 по RFC 6750,
            // а значит символы вне [A-Za-z0-9._~+/=-] отклоняются.
            if (!string.IsNullOrEmpty(UserKey) && !path.StartsWith("/v1/auth/otp"))
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {UserKey}");

            return request;
        }

        private async Task<T> SendAsync<T>(HttpRequestMessage request)
        {
            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request);
            }
            catch (HttpRequestException ex)
            {
                throw new ApiException("CONNECTION_ERROR", ex.Message);
            }
            catch (TaskCanceledException)
            {
                throw new ApiException("TIMEOUT", "Request timed out");
            }

            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                try
                {
                    var error = JsonSerializer.Deserialize<ErrorResponse>(body);
                    if (error?.ServiceInfo != null)
                        throw new ApiException(
                            error.ServiceInfo.ErrorCode ?? "UNKNOWN",
                            error.Message ?? response.ReasonPhrase ?? "Unknown error");
                }
                catch (JsonException) { }

                throw new ApiException(
                    $"HTTP_{(int)response.StatusCode}",
                    response.ReasonPhrase ?? "Unknown error");
            }

            var result = JsonSerializer.Deserialize<T>(body);
            if (result == null)
                throw new ApiException("PARSE_ERROR", "Failed to parse response");

            return result;
        }
    }
}
