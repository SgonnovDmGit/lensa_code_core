using System;
using System.Collections.Generic;
using System.Globalization;
using LenSa.Core.Chat.Models;
using LenSa.Core.Services.Api;

namespace LenSa.Core.Helpers
{
    public sealed record MetricRow(string LabelKey, string Value, string? UnitKey = null);

    public sealed record MetricContextData(
        string TokensText,
        string? LimitText,
        string PercentText,
        double Fill);

    public sealed record MetricCacheSplit(double Fill, string CachedText, string NewText);

    public sealed record MetricCardData(
        string TitleKey,
        string ModelName,
        string? TimeText,
        string? DateText,
        string? CoinsText,
        string? ReasoningEffort,
        MetricContextData? Context,
        IReadOnlyList<MetricRow> WindowRows,
        IReadOnlyList<MetricRow> TokenRows,
        IReadOnlyList<MetricRow> TimeRows,
        MetricCacheSplit? CacheSplit);

    /// <summary>Builds localization-ready display values without accessing WPF resources or services.</summary>
    public static class MetricCardBuilder
    {
        public static MetricCardData Build(ChatMessage message, CultureInfo culture)
        {
            ArgumentNullException.ThrowIfNull(message);
            ArgumentNullException.ThrowIfNull(culture);

            var checkpoint = message.IsCheckpoint;
            var stats = message.Chain ?? message.CallMetrics;
            var timestamp = stats?.CreatedAt;
            var tokenRows = new List<MetricRow>();
            var timeRows = new List<MetricRow>();
            var windowRows = new List<MetricRow>();
            MetricCacheSplit? cacheSplit = null;
            MetricContextData? context = null;

            if (checkpoint)
            {
                AddWindow(windowRows, "str_MetricBefore", message.CheckpointTokensBefore, culture);
                AddWindow(windowRows, "str_MetricAfterEstimate", message.CheckpointWindowTokensAfterEstimate, culture, approximate: true);
                AddWindow(windowRows, "str_MetricAfterActual", message.CheckpointWindowTokensAfterActual, culture);
            }

            if (stats is not null)
            {
                AddUsageRows(stats, culture, tokenRows, out cacheSplit);
                AddTimeRows(stats, culture, timeRows);
                context = checkpoint ? null : BuildContext(stats.ContextFill, culture);
            }

            return new MetricCardData(
                checkpoint ? "str_MetricCompaction" : "str_MetricTurn",
                message.ModelName,
                timestamp?.ToLocalTime().ToString("HH:mm", culture),
                timestamp?.ToLocalTime().ToString("dd.MM.yyyy \u00B7 HH:mm", culture),
                stats?.CostCoins?.ToString("F2", culture),
                checkpoint ? null : NonEmpty(stats?.ReasoningEffort) ?? NonEmpty(message.ReasoningEffort),
                context,
                windowRows.AsReadOnly(),
                tokenRows.AsReadOnly(),
                timeRows.AsReadOnly(),
                cacheSplit);
        }

        private static void AddUsageRows(
            ChainStats stats,
            CultureInfo culture,
            List<MetricRow> rows,
            out MetricCacheSplit? cacheSplit)
        {
            cacheSplit = null;
            if (stats.PromptTokensValue is int promptTokens)
                rows.Add(new MetricRow("str_MetricIncoming", FormatTokens(promptTokens, culture)));

            if (stats.PromptTokensValue is > 0 && stats.CachedTokensValue.HasValue)
            {
                var prompt = stats.PromptTokensValue.Value;
                var cached = Math.Clamp(stats.CachedTokensValue.Value, 0, prompt);
                cacheSplit = new MetricCacheSplit(
                    (double)cached / prompt,
                    FormatTokens(cached, culture),
                    FormatTokens(prompt - cached, culture));
            }
            else if (stats.CachedTokensValue is int cached)
            {
                rows.Add(new MetricRow("str_MetricCached", FormatTokens(cached, culture)));
            }

            if (stats.CacheWriteTokens is int cacheWrite)
                rows.Add(new MetricRow("str_MetricCacheWrite", FormatTokens(cacheWrite, culture)));
            if (stats.CompletionTokensValue is int completion)
                rows.Add(new MetricRow("str_MetricOutgoing", FormatTokens(completion, culture)));

            if (!stats.PromptTokensValue.HasValue && !stats.CompletionTokensValue.HasValue &&
                stats.TotalTokensValue is int total)
                rows.Add(new MetricRow("str_MetricTotalTokens", FormatTokens(total, culture)));
        }

        private static void AddTimeRows(ChainStats stats, CultureInfo culture, List<MetricRow> rows)
        {
            AddSeconds(rows, "str_MetricTotalTime", stats.TurnElapsedMs, culture);
            AddSeconds(rows, "str_MetricGeneration", stats.GenMs, culture);
            if (stats.TokensPerSec is double speed)
                rows.Add(new MetricRow("str_MetricSpeed", speed.ToString("F0", culture), "str_MetricSpeedUnit"));
        }

        private static void AddSeconds(List<MetricRow> rows, string key, long? milliseconds, CultureInfo culture)
        {
            if (milliseconds.HasValue)
                rows.Add(new MetricRow(key, (milliseconds.Value / 1000.0).ToString("F1", culture), "str_MetricSecondsUnit"));
        }

        private static MetricContextData? BuildContext(ContextFillDto? fill, CultureInfo culture)
        {
            if (fill is null) return null;
            var tokens = fill.WindowTokens ?? fill.LastPromptTokens;
            if (!tokens.HasValue) return null;

            var limit = fill.CompactionThresholdTokens is > 0
                ? fill.CompactionThresholdTokens
                : fill.ContextWindow is > 0 ? fill.ContextWindow : null;
            return new MetricContextData(
                FormatTokens(tokens.Value, culture),
                limit.HasValue ? FormatTokens(limit.Value, culture) : null,
                (fill.Fill * 100).ToString("F0", culture),
                fill.Fill);
        }

        private static void AddWindow(List<MetricRow> rows, string key, int? value, CultureInfo culture, bool approximate = false)
        {
            if (value.HasValue)
                rows.Add(new MetricRow(key, (approximate ? "≈" : string.Empty) + FormatTokens(value.Value, culture)));
        }

        private static string FormatTokens(int value, CultureInfo culture) =>
            value.ToString("N0", culture)
                .Replace('\u00A0', ' ')
                .Replace('\u202F', ' ');

        private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
