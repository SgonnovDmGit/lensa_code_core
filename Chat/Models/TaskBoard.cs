using System.Windows;
using LenSa.Core.Helpers;

namespace LenSa.Core.Chat.Models
{
    /// <summary>
    /// Статусы задачи со стороны AI (REQ-021). Отдельная ось от пользовательской
    /// отметки <see cref="TaskItem.UserDone"/>: status — что думает AI, UserDone —
    /// что подтвердил человек.
    /// </summary>
    public static class TaskBoardStatus
    {
        public const string Pending = "pending";
        public const string InProgress = "in_progress";
        public const string Done = "done";
        public const string Blocked = "blocked";
        public const string Cancelled = "cancelled";

        public static bool IsValid(string? status) =>
            status is Pending or InProgress or Done or Blocked or Cancelled;
    }

    /// <summary>
    /// Строка доски задач (T-000080, REQ-021). Ведёт AI через тул task_board_update;
    /// пользователь может лишь поставить/снять галочку <see cref="UserDone"/>.
    /// </summary>
    public class TaskItem : ViewModelBase
    {
        private string _name = string.Empty;
        private string _status = TaskBoardStatus.Pending;
        private bool _userDone;
        private bool _isCurrent;
        private string? _externalId;
        private string? _description;
        private string? _notes;
        private int _ordinal;

        /// <summary>
        /// Ключ мержа тула — назначает AI (или клиент для ручных строк). Модель шлёт что угодно
        /// («step1», «task-2»), поэтому Id — внутренний идентификатор, а пользователю в колонке
        /// «наш №» показывается <see cref="Ordinal"/>. Не путать с <see cref="ExternalId"/>.
        /// </summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>
        /// Порядковый номер строки в доске (1, 2, 3…) — то, что видит человек в колонке «наш №».
        /// Пересчитывается доской при любом изменении состава/порядка.
        /// </summary>
        public int Ordinal
        {
            get => _ordinal;
            set => SetProperty(ref _ordinal, value);
        }

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        /// <summary>Описание задачи — вторая строка под именем.</summary>
        public string? Description
        {
            get => _description;
            set => SetProperty(ref _description, value);
        }

        /// <summary>
        /// Текущая задача: её <see cref="ExternalId"/> подставляется в маркеры кода
        /// вместо {task}. Признак эксклюзивен — держит ChatViewModelBase.
        /// </summary>
        public bool IsCurrent
        {
            get => _isCurrent;
            set => SetProperty(ref _isCurrent, value);
        }

        /// <summary>Статус со стороны AI (см. <see cref="TaskBoardStatus"/>).</summary>
        public string Status
        {
            get => _status;
            set
            {
                if (SetProperty(ref _status, value))
                    OnPropertyChanged(nameof(StatusDisplay));
            }
        }

        /// <summary>Отметка пользователя «выполнено» (галочка в UI).</summary>
        public bool UserDone
        {
            get => _userDone;
            set => SetProperty(ref _userDone, value);
        }

        /// <summary>Номер задачи во внешнем трекере (Jira и т.п.) — не наш Id. Идёт в маркеры кода.</summary>
        public string? ExternalId
        {
            get => _externalId;
            set => SetProperty(ref _externalId, value);
        }

        /// <summary>Короткий комментарий AI к шагу.</summary>
        public string? Notes
        {
            get => _notes;
            set => SetProperty(ref _notes, value);
        }

        /// <summary>Локализованный статус для чипа в строке доски.</summary>
        public string StatusDisplay => Status switch
        {
            TaskBoardStatus.InProgress => FindString("str_TaskBoard_InProgress", "в работе"),
            TaskBoardStatus.Done => FindString("str_TaskBoard_Done", "выполнено"),
            TaskBoardStatus.Blocked => FindString("str_TaskBoard_Blocked", "блокировано"),
            TaskBoardStatus.Cancelled => FindString("str_TaskBoard_Cancelled", "отменено"),
            _ => FindString("str_TaskBoard_Pending", "ожидает")
        };

        private static string FindString(string key, string fallback) =>
            Application.Current?.TryFindResource(key) as string ?? fallback;
    }
}
