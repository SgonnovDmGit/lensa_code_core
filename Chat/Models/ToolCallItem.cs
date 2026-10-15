using LenSa.Core.Helpers;

namespace LenSa.Core.Chat.Models
{
    /// <summary>
    /// Живое состояние вызова тула для карточки в чате (T-000074, DLemma-паттерн):
    /// создаётся из ToolUseDTO со статусом Running, после исполнения handler'а
    /// ChatViewModelBase переводит в Done/Failed и дописывает результат.
    /// </summary>
    public class ToolCallItem : ViewModelBase
    {
        public const string StatusRunning = "running";
        public const string StatusDone = "done";
        public const string StatusFailed = "failed";

        private string _status = StatusRunning;
        private string? _resultMessage;
        private string? _displayTitle;
        private string? _subject;

        public string ToolUseId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Input { get; set; } = string.Empty;

        /// <summary>Optional consumer-provided title for presentation; never persisted.</summary>
        public string? DisplayTitle
        {
            get => _displayTitle;
            set
            {
                if (SetProperty(ref _displayTitle, value))
                    OnPropertyChanged(nameof(HasDisplayTitle));
            }
        }

        public bool HasDisplayTitle => !string.IsNullOrEmpty(_displayTitle);

        /// <summary>Optional consumer-provided target for presentation; never persisted.</summary>
        public string? Subject
        {
            get => _subject;
            set
            {
                if (SetProperty(ref _subject, value))
                    OnPropertyChanged(nameof(HasSubject));
            }
        }

        public bool HasSubject => !string.IsNullOrEmpty(_subject);

        public string Status
        {
            get => _status;
            set
            {
                if (SetProperty(ref _status, value))
                {
                    OnPropertyChanged(nameof(IsRunning));
                    OnPropertyChanged(nameof(IsDone));
                    OnPropertyChanged(nameof(IsFailed));
                }
            }
        }

        public bool IsRunning => _status == StatusRunning;
        public bool IsDone => _status == StatusDone;
        public bool IsFailed => _status == StatusFailed;

        /// <summary>Человекочитаемый итог: Message из ToolResultItem (успех или текст ошибки).</summary>
        public string? ResultMessage
        {
            get => _resultMessage;
            set
            {
                if (SetProperty(ref _resultMessage, value))
                    OnPropertyChanged(nameof(HasResultMessage));
            }
        }

        public bool HasResultMessage => !string.IsNullOrEmpty(_resultMessage);
    }
}
