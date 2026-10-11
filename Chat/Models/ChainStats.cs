using System;
using System.Globalization;
using LenSa.Core.Services.Api;

namespace LenSa.Core.Chat.Models
{
    /// <summary>One round's metrics or the nullable aggregate for an AI turn.</summary>
    public class ChainStats
    {
        private int? _promptTokensValue;
        private int? _completionTokensValue;
        private int? _totalTokensValue;
        private int? _cachedTokensValue;

        public int? PromptTokensValue { get => _promptTokensValue; set => _promptTokensValue = value; }
        public int? CompletionTokensValue { get => _completionTokensValue; set => _completionTokensValue = value; }
        public int? TotalTokensValue { get => _totalTokensValue; set => _totalTokensValue = value; }
        public int? CachedTokensValue { get => _cachedTokensValue; set => _cachedTokensValue = value; }

        // Compatibility accessors for existing bindings and callers that treat missing as zero.
        public int PromptTokens { get => _promptTokensValue ?? 0; set => _promptTokensValue = value; }
        public int CompletionTokens { get => _completionTokensValue ?? 0; set => _completionTokensValue = value; }
        public int TotalTokens => PromptTokens + CompletionTokens;
        public int CachedTokens { get => _cachedTokensValue ?? 0; set => _cachedTokensValue = value; }

        public int? CacheWriteTokens { get; set; }
        public decimal? CostCoins { get; set; }
        public long? GenMs { get; set; }
        public double? TokensPerSec { get; set; }
        public long? TurnElapsedMs { get; set; }
        public DateTimeOffset? CreatedAt { get; set; }
        public string? ReasoningEffort { get; set; }
        public ContextFillDto? ContextFill { get; set; }

        // Legacy display properties remain for existing consumers. MetricCardBuilder uses only
        // provider-reported TokensPerSec and TurnElapsedMs.
        public double WallSeconds { get; set; }
        public double TokensPerSecond => WallSeconds > 0.1 ? CompletionTokens / WallSeconds : 0;
        public string SpeedDisplay => $"{TokensPerSecond:F1}";
        public string WallDisplay => WallSeconds >= 90
            ? $"{(int)(WallSeconds / 60)}:{(int)(WallSeconds % 60):00}"
            : $"{WallSeconds:F0} {SecondsUnit()}";
        public string CoinsDisplay => CostCoins.HasValue ? $"{CostCoins.Value:F2}" : "—";
        public bool HasCoins => CostCoins.HasValue;
        public bool HasCached => CachedTokensValue is > 0;
        public string CachedDisplay => PromptTokensValue is > 0
            ? $"{CachedTokens:N0} ({100.0 * CachedTokens / PromptTokensValue.Value:F0}%)"
            : $"{CachedTokens:N0}";

        private static string SecondsUnit() =>
            System.Windows.Application.Current?.TryFindResource("str_UnitSeconds") as string ?? "s";

        public static ChainStats FromResponse(ChatResponse response, DateTimeOffset receivedAt) => new()
        {
            PromptTokensValue = response.PromptTokensValue,
            CompletionTokensValue = response.CompletionTokensValue,
            TotalTokensValue = response.TotalTokensValue,
            CachedTokensValue = response.CachedTokens,
            CacheWriteTokens = response.CacheWriteTokens,
            CostCoins = response.CostCoins,
            GenMs = response.GenMs,
            TokensPerSec = response.TokensPerSec,
            TurnElapsedMs = response.TurnElapsedMs,
            CreatedAt = receivedAt,
            ReasoningEffort = response.ReasoningEffort,
            ContextFill = Clone(response.ContextFill)
        };

        public static ChainStats FromRetry(ChatToolRetryResponse response, DateTimeOffset receivedAt) => new()
        {
            PromptTokensValue = response.PromptTokensValue,
            CompletionTokensValue = response.CompletionTokensValue,
            TotalTokensValue = response.TotalTokensValue,
            CachedTokensValue = response.CachedTokens,
            CacheWriteTokens = response.CacheWriteTokens,
            CostCoins = response.CostCoins,
            GenMs = response.GenMs,
            TokensPerSec = response.TokensPerSec,
            TurnElapsedMs = response.TurnElapsedMs,
            CreatedAt = receivedAt,
            ReasoningEffort = response.ReasoningEffort,
            ContextFill = Clone(response.ContextFill)
        };

        public static ChainStats FromHistory(SessionMessageDto message)
        {
            DateTimeOffset? createdAt = DateTimeOffset.TryParse(message.CreatedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed) ? parsed : null;
            return new ChainStats
            {
                PromptTokensValue = message.PromptTokens,
                CompletionTokensValue = message.CompletionTokens,
                TotalTokensValue = message.TotalTokens,
                CachedTokensValue = message.CachedTokens,
                CacheWriteTokens = message.CacheWriteTokens,
                CostCoins = message.CostCoins,
                GenMs = message.GenMs,
                TokensPerSec = message.TokensPerSec,
                TurnElapsedMs = message.TurnElapsedMs,
                CreatedAt = createdAt,
                ReasoningEffort = message.ReasoningEffort,
                ContextFill = Clone(message.ContextFill)
            };
        }

        public static ChainStats FromLegacyMessage(ChatMessage message) => new()
        {
            PromptTokensValue = message.PromptTokens,
            CompletionTokensValue = message.CompletionTokens,
            TotalTokensValue = message.TotalTokens,
            CachedTokensValue = message.CachedTokens,
            CostCoins = message.CostCoins
        };

        /// <summary>Returns a new aggregate; final-round snapshots never leak from prior rounds.</summary>
        public static ChainStats Accumulate(ChainStats? aggregate, ChainStats round, bool isFinal)
        {
            ArgumentNullException.ThrowIfNull(round);
            return new ChainStats
            {
                PromptTokensValue = Add(aggregate?.PromptTokensValue, round.PromptTokensValue),
                CompletionTokensValue = Add(aggregate?.CompletionTokensValue, round.CompletionTokensValue),
                TotalTokensValue = Add(aggregate?.TotalTokensValue, round.TotalTokensValue),
                CachedTokensValue = Add(aggregate?.CachedTokensValue, round.CachedTokensValue),
                CacheWriteTokens = Add(aggregate?.CacheWriteTokens, round.CacheWriteTokens),
                CostCoins = Add(aggregate?.CostCoins, round.CostCoins),
                GenMs = isFinal ? round.GenMs : null,
                TokensPerSec = isFinal ? round.TokensPerSec : null,
                TurnElapsedMs = isFinal ? round.TurnElapsedMs : null,
                CreatedAt = isFinal ? round.CreatedAt : null,
                ReasoningEffort = isFinal ? round.ReasoningEffort : null,
                ContextFill = isFinal ? Clone(round.ContextFill) : null,
                WallSeconds = (aggregate?.WallSeconds ?? 0) + round.WallSeconds
            };
        }

        public static ContextFillDto? Clone(ContextFillDto? value) => value is null ? null : new ContextFillDto
        {
            Model = value.Model,
            LastPromptTokens = value.LastPromptTokens,
            WindowTokens = value.WindowTokens,
            WindowTokensSource = value.WindowTokensSource,
            ContextWindow = value.ContextWindow,
            CompactionThresholdTokens = value.CompactionThresholdTokens,
            FillRatio = value.FillRatio,
            ThresholdRatio = value.ThresholdRatio
        };

        private static int? Add(int? left, int? right) => left.HasValue || right.HasValue
            ? (left ?? 0) + (right ?? 0)
            : null;

        private static decimal? Add(decimal? left, decimal? right) => left.HasValue || right.HasValue
            ? (left ?? 0m) + (right ?? 0m)
            : null;
    }
}
