using System.Collections.Generic;
using System.Collections.ObjectModel;
using LenSa.Core.Helpers;

namespace LenSa.Core.Chat.Models
{
    public static class MessageRoles
    {
        public const string User = "user";
        public const string Assistant = "assistant";
        public const string ToolUse = "tool_use";
        public const string ToolResult = "tool_result";
        public const string Debug = "debug";
        public const string Notice = "notice";

        /// <summary>
        /// Синтетическая строка истории (ANNOUNCE-005): сервер сжал старый контекст в
        /// rolling-summary. Не сообщение — индикатор «здесь контекст свёрнут».
        /// </summary>
        public const string Checkpoint = "checkpoint";
    }

    public static class ReasoningEfforts
    {
        public const string None = "none";
        public const string Minimal = "minimal";
        public const string Low = "low";
        public const string Medium = "medium";
        public const string High = "high";
    }

    public class ChatMessage : ViewModelBase
    {
        private string _content = string.Empty;
        private string _modelName = string.Empty;
        private string _providerName = string.Empty;
        private bool _isThinking;
        private bool _hasCodeChanges;
        private List<ToolUseDTO>? _toolUses;
        private List<ToolResultItem>? _toolResults;
        private string? _reasoningText;
        private string? _reasoningEffort;
        private int _promptTokens;
        private int _completionTokens;
        private int _totalTokens;
        private decimal? _costCoins;
        private decimal? _costUsd;
        private decimal? _costRub;
        private decimal? _balanceCoins;
        private int? _userMessageId;
        private bool _isStreaming;
        private bool _isInterrupted;
        private string? _checkpointTrigger;
        private int? _checkpointTokensBefore;
        private int? _checkpointWindowTokensAfterEstimate;
        private int? _checkpointWindowTokensAfterActual;
        private bool _isSummaryExpanded;

        public string Role { get; set; } = string.Empty;

        public string Content
        {
            get => _content;
            set => SetProperty(ref _content, value);
        }

        public string ProviderName
        {
            get => _providerName;
            set => SetProperty(ref _providerName, value);
        }

        public string ModelName
        {
            get => _modelName;
            set => SetProperty(ref _modelName, value);
        }

        public List<ToolUseDTO>? ToolUses
        {
            get => _toolUses;
            set => SetProperty(ref _toolUses, value);
        }

        public List<ToolResultItem>? ToolResults
        {
            get => _toolResults;
            set => SetProperty(ref _toolResults, value);
        }

        /// <summary>
        /// Карточки вызовов тулов (T-000074): живые статусы running → done/failed.
        /// Заполняется ChatViewModelBase.AttachToolCalls из ToolUses; результаты
        /// подтягивает DispatchToolUsesAsync по ToolUseId.
        /// </summary>
        public ObservableCollection<ToolCallItem> ToolCalls { get; } = new();

        // === Аналитика цепочки (T-000074): агрегат по ходу контент→тул→…→контент.
        // Ставится только на ФИНАЛЬНОЕ сообщение цепочки (без toolUses) — на нём
        // рендерится чип «i» с двухъярусным хинтом Токены/Затраты.
        private ChainStats? _chain;

        public ChainStats? Chain
        {
            get => _chain;
            set
            {
                if (SetProperty(ref _chain, value))
                    OnPropertyChanged(nameof(HasChain));
            }
        }

        public bool HasChain => _chain != null;

        // Reasoning (REQ-007 / REQ-008, server v0.59.0). Пустые ⟺ ход без размышлений.
        public string? ReasoningText
        {
            get => _reasoningText;
            set
            {
                if (SetProperty(ref _reasoningText, value))
                    OnPropertyChanged(nameof(HasReasoning));
            }
        }

        // Echo фактически применённого effort'а (Router клэмпит на не-reasoning моделях).
        public string? ReasoningEffort
        {
            get => _reasoningEffort;
            set => SetProperty(ref _reasoningEffort, value);
        }

        public bool HasReasoning => !string.IsNullOrEmpty(_reasoningText);

        public int PromptTokens
        {
            get => _promptTokens;
            set
            {
                if (SetProperty(ref _promptTokens, value))
                    OnPropertyChanged(nameof(HasTokens));
            }
        }

        /// <summary>
        /// Prompt-токены хода, взятые из кэша (REQ-027). ⊆ PromptTokens. Ноль — «кэша не было
        /// ИЛИ провайдер не отдал разбивку»: сервер эти два случая не различает и ноль не шлёт
        /// вовсе, поэтому и мы строку прячем, а не пишем «0 из кэша» (T-000146).
        /// </summary>
        private int _cachedTokens;
        public int CachedTokens
        {
            get => _cachedTokens;
            set => SetProperty(ref _cachedTokens, value);
        }

        public int CompletionTokens
        {
            get => _completionTokens;
            set
            {
                if (SetProperty(ref _completionTokens, value))
                    OnPropertyChanged(nameof(HasTokens));
            }
        }

        public int TotalTokens
        {
            get => _totalTokens;
            set => SetProperty(ref _totalTokens, value);
        }

        public bool HasTokens => _promptTokens > 0 || _completionTokens > 0;

        // Cost per-message. Nullable — отсутствие ≠ «0», а «неизвестно»
        // (биллинг off / цена модели не посчитана / commit failed).
        public decimal? CostCoins
        {
            get => _costCoins;
            set
            {
                if (SetProperty(ref _costCoins, value))
                    OnPropertyChanged(nameof(HasBilling));
            }
        }

        public decimal? CostUsd
        {
            get => _costUsd;
            set => SetProperty(ref _costUsd, value);
        }

        public decimal? CostRub
        {
            get => _costRub;
            set => SetProperty(ref _costRub, value);
        }

        public bool HasBilling => _costCoins.HasValue;

        // Баланс пользователя ПОСЛЕ списания этого хода — для обновления шапки без /me запроса.
        public decimal? BalanceCoins
        {
            get => _balanceCoins;
            set => SetProperty(ref _balanceCoins, value);
        }

        // SERIAL id user-message на сервере (T-000107) — стабильный якорь для retry / attribution.
        public int? UserMessageId
        {
            get => _userMessageId;
            set => SetProperty(ref _userMessageId, value);
        }

        // Streaming flags — модель готова, ChatViewModelBase подключит в T-000067.
        public bool IsStreaming
        {
            get => _isStreaming;
            set => SetProperty(ref _isStreaming, value);
        }

        public bool IsInterrupted
        {
            get => _isInterrupted;
            set => SetProperty(ref _isInterrupted, value);
        }

        public bool IsUser => Role == MessageRoles.User;
        public bool IsAssistant => Role == MessageRoles.Assistant;
        public bool IsToolUse => Role == MessageRoles.ToolUse;
        public bool IsToolResult => Role == MessageRoles.ToolResult;
        public bool IsDebug => Role == MessageRoles.Debug;
        public bool IsCheckpoint => Role == MessageRoles.Checkpoint;
        public bool IsNotice => Role == MessageRoles.Notice;

        public string? CheckpointTrigger
        {
            get => _checkpointTrigger;
            set => SetProperty(ref _checkpointTrigger, value);
        }

        public int? CheckpointTokensBefore
        {
            get => _checkpointTokensBefore;
            set
            {
                if (SetProperty(ref _checkpointTokensBefore, value))
                    OnPropertyChanged(nameof(HasCheckpointReduction));
            }
        }

        public int? CheckpointWindowTokensAfterEstimate
        {
            get => _checkpointWindowTokensAfterEstimate;
            set
            {
                if (SetProperty(ref _checkpointWindowTokensAfterEstimate, value))
                {
                    OnPropertyChanged(nameof(CheckpointTokensAfter));
                    OnPropertyChanged(nameof(HasCheckpointReduction));
                }
            }
        }

        public int? CheckpointWindowTokensAfterActual
        {
            get => _checkpointWindowTokensAfterActual;
            set
            {
                if (SetProperty(ref _checkpointWindowTokensAfterActual, value))
                {
                    OnPropertyChanged(nameof(CheckpointTokensAfter));
                    OnPropertyChanged(nameof(HasCheckpointReduction));
                }
            }
        }

        public int? CheckpointTokensAfter =>
            CheckpointWindowTokensAfterActual ?? CheckpointWindowTokensAfterEstimate;

        public bool HasCheckpointReduction =>
            CheckpointTokensBefore is int before && CheckpointTokensAfter is int after && after < before;

        public bool IsSummaryExpanded
        {
            get => _isSummaryExpanded;
            set => SetProperty(ref _isSummaryExpanded, value);
        }

        public bool HasCodeChanges
        {
            get => _hasCodeChanges;
            set => SetProperty(ref _hasCodeChanges, value);
        }

        public bool IsThinking
        {
            get => _isThinking;
            set
            {
                if (SetProperty(ref _isThinking, value))
                    OnPropertyChanged(nameof(CanQuote));
            }
        }

        public bool CanQuote => !IsUser && !IsDebug && !IsThinking && !IsToolUse && !IsToolResult &&
                                !IsCheckpoint && !IsNotice;
    }
}
