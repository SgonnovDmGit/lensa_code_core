using System;

namespace LenSa.Core.Chat.Models
{
    /// <summary>
    /// Агрегат одного хода AI (T-000074): контент→тул→…→контент = одна цепочка.
    /// Время — полное wall-time от отправки запроса до финального контента,
    /// включая исполнение тулов на клиенте (решение юзера от 2026-07-06).
    /// </summary>
    public class ChainStats
    {
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public int TotalTokens => PromptTokens + CompletionTokens;

        /// <summary>
        /// Сколько prompt-токенов цепочки пришло из кэша (REQ-027). Именно это объясняет
        /// пользователю, почему длинный ход с тулами стоил дёшево: на повторных раундах
        /// history + system prompt читаются из кэша, а он в разы дешевле входа.
        /// </summary>
        public int CachedTokens { get; set; }

        public bool HasCached => CachedTokens > 0;

        /// <summary>«6 528 (49%)» — доля от входа цепочки, иначе число не с чем сравнить.</summary>
        public string CachedDisplay => PromptTokens > 0
            ? $"{CachedTokens:N0} ({100.0 * CachedTokens / PromptTokens:F0}%)"
            : $"{CachedTokens:N0}";
        public decimal? CostCoins { get; set; }
        public double WallSeconds { get; set; }

        /// <summary>Токены ответа / полное wall-time хода.</summary>
        public double TokensPerSecond =>
            WallSeconds > 0.1 ? CompletionTokens / WallSeconds : 0;

        // Единица измерения — в подписи хинта (str_ChainSpeed «скорость, ток/с»),
        // значение — только число, чтобы цифры яруса стояли на одном уровне.
        public string SpeedDisplay => $"{TokensPerSecond:F1}";

        // До 90с — «38 с» (единица локализована), дальше — «2:05».
        public string WallDisplay => WallSeconds >= 90
            ? $"{(int)(WallSeconds / 60)}:{(int)(WallSeconds % 60):00}"
            : $"{WallSeconds:F0} {SecondsUnit()}";

        private static string SecondsUnit() =>
            System.Windows.Application.Current?.TryFindResource("str_UnitSeconds") as string ?? "s";
        public string CoinsDisplay => CostCoins.HasValue ? $"{CostCoins.Value:F2}" : "—";
        public bool HasCoins => CostCoins.HasValue;
    }
}
