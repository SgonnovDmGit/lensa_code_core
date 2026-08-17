using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using LenSa.Core.Chat.Models;
using LenSa.Core.Chat.Services;
using LenSa.Core.Helpers;
using LenSa.Core.Services.Api;

namespace LenSa.Core.Chat.ViewModels
{
    /// <summary>
    /// Базовый VM чата для продуктовых наследников (1С AiViewModel, Universal AiViewModel).
    /// Держит общее: коллекцию Messages, session id, tool dispatch dictionary, команды.
    /// Наследник переопределяет BuildRequest (свой контекст: ProjectSettings, OneCSettings и т.д.)
    /// и регистрирует свои tool-handlers через RegisterToolHandler.
    /// </summary>
    public abstract class ChatViewModelBase : ViewModelBase
    {
        protected readonly ApiClient Api;

        private readonly Dictionary<string, Func<ToolUseDTO, Task<ToolResultItem>>> _toolHandlers =
            new(StringComparer.Ordinal);

        private string _inputText = string.Empty;
        private string _sessionId = string.Empty;
        private bool _isBusy;

        // CTS активного SendMessageAsync — заменяется на новый в каждом заходе,
        // используется InterruptCommand для отмены submit+poll (ANNOUNCE-007).
        private CancellationTokenSource? _currentSendCts;

        // === Аналитика цепочки (T-000074) ==================================
        // Ход = user-запрос → (контент+тулы → tool-results → …) → финальный контент.
        // Старт в SendMessageAsync, накопление в ChainAccumulate, финал ставит
        // ChainStats на последнее сообщение (там рендерится чип «i»).
        private DateTime _chainStartUtc;
        private int _chainPromptTokens;
        private int _chainCompletionTokens;
        private int _chainCachedTokens;
        private decimal? _chainCoins;

        /// <summary>Сброс агрегата цепочки — начало нового хода.</summary>
        protected void ChainStart()
        {
            _chainStartUtc = DateTime.UtcNow;
            _chainPromptTokens = 0;
            _chainCompletionTokens = 0;
            _chainCachedTokens = 0;
            _chainCoins = null;
        }

        /// <summary>
        /// Накопить раунд цепочки; если это финальный раунд (нет toolUses) —
        /// проставить msg.Chain для чипа аналитики. Зовётся из Handle*ResponseAsync
        /// базы И из продуктовых override'ов (AiViewModel ведёт свой pipeline).
        /// </summary>
        protected void ChainAccumulate(ChatMessage msg, bool isFinal)
        {
            _chainPromptTokens += msg.PromptTokens;
            _chainCompletionTokens += msg.CompletionTokens;
            _chainCachedTokens += msg.CachedTokens;
            if (msg.CostCoins.HasValue)
                _chainCoins = (_chainCoins ?? 0m) + msg.CostCoins.Value;

            if (!isFinal) return;

            msg.Chain = new ChainStats
            {
                PromptTokens = _chainPromptTokens,
                CompletionTokens = _chainCompletionTokens,
                CachedTokens = _chainCachedTokens,
                CostCoins = _chainCoins,
                WallSeconds = (DateTime.UtcNow - _chainStartUtc).TotalSeconds
            };
        }

        /// <summary>
        /// Наполнить msg.ToolCalls карточками из toolUses (статус running).
        /// DispatchToolUsesAsync найдёт их по ToolUseId и переведёт в done/failed.
        /// </summary>
        protected void AttachToolCalls(ChatMessage msg)
        {
            if (msg.ToolUses is not { Count: > 0 }) return;
            msg.ToolCalls.Clear();
            foreach (var tu in msg.ToolUses)
            {
                msg.ToolCalls.Add(new ToolCallItem
                {
                    ToolUseId = tu.Id,
                    Name = tu.Name,
                    Input = tu.Input.ValueKind == System.Text.Json.JsonValueKind.Undefined
                        ? string.Empty
                        : tu.Input.ToString()
                });
            }
            _activeToolCallsMessage = msg;
        }

        // Сообщение, чьи ToolCalls сейчас исполняются — DispatchToolUsesAsync обновляет статусы.
        private ChatMessage? _activeToolCallsMessage;

        public ObservableCollection<ChatMessage> Messages { get; } = new();

        public string InputText
        {
            get => _inputText;
            set => SetProperty(ref _inputText, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    OnBusyChanged(value);
                    // WPF переопрашивает CanExecute лениво (по input-событиям) —
                    // без форса кнопки на !IsBusy остаются серыми до движения мыши
                    // (смок 07.07: «+» после выбора диалога временно неактивна).
                    System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        /// <summary>Hook для наследника — переключение loading-индикатора / thinking-bubble.</summary>
        protected virtual void OnBusyChanged(bool busy) { }

        public string SessionId
        {
            get => _sessionId;
            protected set
            {
                var previous = _sessionId;
                if (!SetProperty(ref _sessionId, value)) return;

                // Доску завели до первого сообщения (сессии ещё не было) — сервер выдал id,
                // переносим черновик под него, иначе он потерялся бы при перезапуске.
                if (string.IsNullOrEmpty(previous) && !string.IsNullOrEmpty(value) && TaskBoard.Count > 0)
                    PersistTaskBoard();
            }
        }

        public ICommand SendMessageCommand { get; }
        public ICommand NewChatCommand { get; }

        /// <summary>Новый диалог, доска переезжает (T-000159). Неактивна, когда доска пуста.</summary>
        public ICommand NewChatWithBoardCommand { get; }
        public ICommand InterruptCommand { get; }

        /// <summary>Ручное сжатие контекста диалога (T-000133).</summary>
        public ICommand CompactContextCommand { get; }

        /// <summary>
        /// «→ в песочницу» на код-блоке ответа (T-000096). Базовый чат песочницы не знает —
        /// продукт переопределяет. null (Universal) → кнопки на блоках не рисуются.
        /// </summary>
        public virtual ICommand? SendCodeToSandboxCommand => null;

        protected ChatViewModelBase(ApiClient api)
        {
            Api = api ?? throw new ArgumentNullException(nameof(api));

            SendMessageCommand = new RelayCommand(
                () => _ = SendMessageAsync(InputText, "free_form"),
                () => !string.IsNullOrWhiteSpace(InputText) && !IsBusy);

            // T-000133: сжать контекст руками, не дожидаясь автотриггера у самого потолка.
            CompactContextCommand = new RelayCommand(
                () => _ = CompactContextAsync(),
                () => !string.IsNullOrEmpty(SessionId) && !IsBusy);

            NewChatCommand = new RelayCommand(NewChat, () => !IsBusy);

            NewChatWithBoardCommand = new RelayCommand(
                NewChatWithBoard, () => !IsBusy && TaskBoard.Count > 0);

            InterruptCommand = new RelayCommand(
                Interrupt,
                () => IsBusy && _currentSendCts is { IsCancellationRequested: false });

            // Доска задач — общая для обоих продуктов (T-000081, REQ-021)
            RegisterToolHandler(TaskBoardToolName, HandleTaskBoardUpdate);
            TaskBoard.CollectionChanged += (_, _) => OnTaskBoardChanged();
            ToggleTaskBoardCommand = new RelayCommand(
                () => IsTaskBoardExpanded = !IsTaskBoardExpanded);

            // Доска живёт и без AI: юзер сам добавляет строки, правит имя, крутит статус
            AddTaskCommand = new RelayCommand(AddTaskManually);
            RemoveTaskCommand = new RelayCommand<TaskItem>(RemoveTask);
            CycleTaskStatusCommand = new RelayCommand<TaskItem>(CycleTaskStatus);
            SetCurrentTaskCommand = new RelayCommand<TaskItem>(SetCurrentTask);
            SetBoardModeCommand = new RelayCommand<string>(m =>
            {
                if (!string.IsNullOrEmpty(m)) BoardMode = m!;
            });
        }

        // === Доска задач (T-000080/T-000081, REQ-021) =========================

        private const string TaskBoardToolName = "task_board_update";

        private bool _isTaskBoardExpanded = true;

        /// <summary>Строки доски — ведёт AI через тул task_board_update.</summary>
        public ObservableCollection<TaskItem> TaskBoard { get; } = new();

        // --- Режимы развёртки: доска не должна съедать чат, когда задач много ---

        public const string BoardModeCurrent = "current";  // только строка, помеченная ●
        public const string BoardModeThree = "three";      // три строки + скролл (дефолт)
        public const string BoardModeAll = "all";          // все, но не выше половины чата

        private string _boardMode = BoardModeThree;
        private string _taskFilter = string.Empty;
        private string _taskSort = TaskSortManual;

        public const string TaskSortManual = "manual";
        public const string TaskSortStatus = "status";
        public const string TaskSortTracker = "tracker";

        /// <summary>Режим развёртки доски: current / three / all.</summary>
        public string BoardMode
        {
            get => _boardMode;
            set
            {
                if (!SetProperty(ref _boardMode, value)) return;
                OnPropertyChanged(nameof(IsBoardModeCurrent));
                OnPropertyChanged(nameof(IsBoardModeThree));
                OnPropertyChanged(nameof(IsBoardModeAll));
                OnPropertyChanged(nameof(IsFilterBarVisible));
                TasksView.Refresh();
            }
        }

        public bool IsBoardModeCurrent => BoardMode == BoardModeCurrent;
        public bool IsBoardModeThree => BoardMode == BoardModeThree;
        public bool IsBoardModeAll => BoardMode == BoardModeAll;

        /// <summary>Поиск по имени / описанию / трекеру.</summary>
        public string TaskFilter
        {
            get => _taskFilter;
            set { if (SetProperty(ref _taskFilter, value)) TasksView.Refresh(); }
        }

        /// <summary>Сортировка: manual (ручной порядок) / status / tracker.</summary>
        public string TaskSort
        {
            get => _taskSort;
            set { if (SetProperty(ref _taskSort, value)) ApplySort(); }
        }

        /// <summary>
        /// Панель поиска/сортировки: только в режиме «все» и только когда задач правда много —
        /// на четырёх строках это мёртвый груз.
        /// </summary>
        public bool IsFilterBarVisible => IsBoardModeAll && TaskBoard.Count > 6;

        private System.ComponentModel.ICollectionView? _tasksView;

        /// <summary>Отображаемый срез доски: режим + фильтр + сортировка.</summary>
        public System.ComponentModel.ICollectionView TasksView
        {
            get
            {
                if (_tasksView != null) return _tasksView;

                _tasksView = System.Windows.Data.CollectionViewSource.GetDefaultView(TaskBoard);
                _tasksView.Filter = o =>
                {
                    if (o is not TaskItem t) return false;

                    // Режим «текущая» — показываем только помеченную строку
                    if (IsBoardModeCurrent && !t.IsCurrent) return false;

                    if (string.IsNullOrWhiteSpace(TaskFilter)) return true;

                    var q = TaskFilter.Trim();
                    const StringComparison ic = StringComparison.CurrentCultureIgnoreCase;
                    return (t.Name?.Contains(q, ic) ?? false)
                        || (t.Description?.Contains(q, ic) ?? false)
                        || (t.ExternalId?.Contains(q, ic) ?? false);
                };
                return _tasksView;
            }
        }

        private void ApplySort()
        {
            var view = TasksView;
            using (view.DeferRefresh())
            {
                view.SortDescriptions.Clear();
                switch (TaskSort)
                {
                    case TaskSortStatus:
                        view.SortDescriptions.Add(new System.ComponentModel.SortDescription(
                            nameof(TaskItem.Status), System.ComponentModel.ListSortDirection.Ascending));
                        break;
                    case TaskSortTracker:
                        view.SortDescriptions.Add(new System.ComponentModel.SortDescription(
                            nameof(TaskItem.ExternalId), System.ComponentModel.ListSortDirection.Ascending));
                        break;
                    // manual — без SortDescriptions: остаётся порядок коллекции (drag&drop)
                }
            }
        }

        /// <summary>Доска показывается только когда AI её завёл.</summary>
        public bool HasTaskBoard => TaskBoard.Count > 0;

        /// <summary>Панель развёрнута (иначе — одна строка «Задачи 2/5 ▾»).</summary>
        public bool IsTaskBoardExpanded
        {
            get => _isTaskBoardExpanded;
            set => SetProperty(ref _isTaskBoardExpanded, value);
        }

        /// <summary>Счётчик для схлопнутого заголовка: закрытых из всех.</summary>
        public string TaskBoardSummary
        {
            get
            {
                var done = 0;
                foreach (var t in TaskBoard)
                    if (t.UserDone || t.Status == TaskBoardStatus.Done) done++;
                return $"{done}/{TaskBoard.Count}";
            }
        }

        public ICommand ToggleTaskBoardCommand { get; }

        /// <summary>Ручное управление доской — она работает и без AI (тула может не быть).</summary>
        public ICommand AddTaskCommand { get; }
        public ICommand RemoveTaskCommand { get; }
        public ICommand CycleTaskStatusCommand { get; }
        public ICommand SetCurrentTaskCommand { get; }
        public ICommand SetBoardModeCommand { get; }

        /// <summary>Текущая задача доски — источник {task} для маркеров кода.</summary>
        public TaskItem? CurrentTask => TaskBoard.FirstOrDefault(t => t.IsCurrent);

        /// <summary>
        /// Отметить строку текущей (эксклюзивно). Повторный клик по текущей — снимает отметку.
        /// </summary>
        private void SetCurrentTask(TaskItem? item)
        {
            if (item == null) return;

            var turnOff = item.IsCurrent;
            foreach (var t in TaskBoard)
                t.IsCurrent = !turnOff && ReferenceEquals(t, item);

            OnPropertyChanged(nameof(CurrentTask));
            OnCurrentTaskChanged(CurrentTask);

            // В режиме «текущая» показывается ровно помеченная строка — срез пересобрать
            if (IsBoardModeCurrent) TasksView.Refresh();
        }

        /// <summary>
        /// Hook для продукта: текущая задача сменилась — её номер трекера должен уехать
        /// в профиль маркеров ({task}). Core про профиль маркеров не знает.
        /// </summary>
        protected virtual void OnCurrentTaskChanged(TaskItem? current) { }

        // Счётчик id для строк, заведённых руками (AI назначает свои id сам)
        private int _manualTaskSeq;

        private void AddTaskManually()
        {
            string id;
            do { id = $"u{++_manualTaskSeq}"; }
            while (TaskBoard.Any(t => t.Id == id));

            var item = new TaskItem
            {
                Id = id,
                Name = FindString("str_TaskBoard_NewTask", "Новая задача"),
                Status = TaskBoardStatus.Pending
            };
            item.PropertyChanged += OnTaskItemChanged;
            TaskBoard.Add(item);
            IsTaskBoardExpanded = true;
        }

        private void RemoveTask(TaskItem? item)
        {
            if (item == null) return;

            // Спрашиваем: в строке живёт описание, случайно потерять его обидно.
            var answer = System.Windows.MessageBox.Show(
                string.Format(
                    FindString("str_TaskBoard_RemoveConfirm", "Удалить задачу «{0}»?"),
                    item.Name),
                FindString("str_TaskBoard_RemoveConfirmTitle", "Удаление задачи"),
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            if (answer != System.Windows.MessageBoxResult.Yes) return;

            item.PropertyChanged -= OnTaskItemChanged;
            TaskBoard.Remove(item);
        }

        /// <summary>
        /// Перестановка строки drag&amp;drop'ом. Ручной порядок — «родной» порядок коллекции;
        /// сортировка по статусу/трекеру его не рушит (это отдельный вид, см. TasksView).
        /// </summary>
        public void MoveTask(TaskItem? dragged, TaskItem? target) => MoveTask(dragged, target, false);

        /// <param name="insertBefore">
        /// Курсор в верхней половине целевой строки — значит бросили ПЕРЕД ней, иначе после.
        /// Ровно то место, где при перетаскивании рисовалась линия вставки (T-000127).
        /// </param>
        public void MoveTask(TaskItem? dragged, TaskItem? target, bool insertBefore)
        {
            if (dragged == null || target == null || ReferenceEquals(dragged, target)) return;

            var from = TaskBoard.IndexOf(dragged);
            var to = TaskBoard.IndexOf(target);
            if (from < 0 || to < 0) return;

            if (!insertBefore) to++;
            if (from < to) to--;          // строка уезжает со своего места — индекс справа сдвигается
            if (to < 0) to = 0;
            if (to >= TaskBoard.Count) to = TaskBoard.Count - 1;
            if (from == to) return;

            TaskBoard.Move(from, to);
        }

        /// <summary>
        /// Клик по чипу статуса перебирает ожидает → в работе → выполнено (по кругу).
        /// Blocked/cancelled ставит AI — руками они нужны редко, в цикл не включены.
        /// </summary>
        private void CycleTaskStatus(TaskItem? item)
        {
            if (item == null) return;
            item.Status = item.Status switch
            {
                TaskBoardStatus.Pending => TaskBoardStatus.InProgress,
                TaskBoardStatus.InProgress => TaskBoardStatus.Done,
                _ => TaskBoardStatus.Pending
            };
        }

        private void OnTaskBoardChanged()
        {
            Renumber();
            PersistTaskBoard();
            OnPropertyChanged(nameof(HasTaskBoard));
            OnPropertyChanged(nameof(TaskBoardSummary));
            OnPropertyChanged(nameof(IsFilterBarVisible));
        }

        // Доска — состояние клиента (сервер её не хранит, REQ-021). Пишем на диск при любом
        // изменении: состав, порядок, статус, галочка, трекер. Файл крошечный, дебаунс не нужен.
        private bool _suppressPersist;

        private void PersistTaskBoard()
        {
            if (_suppressPersist || string.IsNullOrEmpty(SessionId)) return;
            TaskBoardStore.Save(SessionId, TaskBoard);
        }

        /// <summary>
        /// Поднять доску открываемого диалога (T-000131). Зовёт продукт при переключении
        /// диалога — сразу после того, как выставлен SessionId.
        /// </summary>
        protected void RestoreTaskBoard(string sessionId)
        {
            _suppressPersist = true;
            _suppressUserDoneEcho = true;
            try
            {
                foreach (var old in TaskBoard) old.PropertyChanged -= OnTaskItemChanged;
                TaskBoard.Clear();

                foreach (var item in TaskBoardStore.Load(sessionId))
                {
                    item.PropertyChanged += OnTaskItemChanged;
                    TaskBoard.Add(item);
                }

                Renumber();
                OnPropertyChanged(nameof(CurrentTask));
                OnCurrentTaskChanged(CurrentTask);
                TasksView.Refresh();
            }
            finally
            {
                _suppressPersist = false;
                _suppressUserDoneEcho = false;
            }
        }

        /// <summary>
        /// «Наш №» — позиция в доске, а не Id модели: та шлёт «step1»/«task-2», и показывать
        /// это человеку бессмысленно (смок 12.07). Id остаётся ключом мержа тула.
        /// </summary>
        private void Renumber()
        {
            for (var i = 0; i < TaskBoard.Count; i++)
                TaskBoard[i].Ordinal = i + 1;
        }

        /// <summary>
        /// Если текущая не выбрана — метим первую незавершённую. Иначе после плана от AI
        /// доска есть, а маркеры кода не знают номера задачи, пока человек не ткнёт ● руками.
        /// </summary>
        private void EnsureCurrentTask()
        {
            if (TaskBoard.Any(t => t.IsCurrent)) return;

            var next = TaskBoard.FirstOrDefault(t =>
                t.Status != TaskBoardStatus.Done && t.Status != TaskBoardStatus.Cancelled);
            if (next == null) return;

            next.IsCurrent = true;
            OnPropertyChanged(nameof(CurrentTask));
            OnCurrentTaskChanged(next);
            if (IsBoardModeCurrent) TasksView.Refresh();
        }

        /// <summary>
        /// Галочка пользователя — отдельная ось от статуса AI. Отметка уходит в AI
        /// сразу новым ходом (клиент не может инициировать tool_use, только сообщение).
        /// Пока AI занят — ход не шлём: актуальная доска и так вернётся ему в success
        /// ближайшего task_board_update (в payload отдаётся вся доска с UserDone).
        /// </summary>
        private void OnTaskItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (sender is not TaskItem item) return;

            // Ordinal — производная от позиции, её пересчитывает Renumber; сохранять по ней незачем
            if (e.PropertyName != nameof(TaskItem.Ordinal))
                PersistTaskBoard();

            if (e.PropertyName == nameof(TaskItem.Status) || e.PropertyName == nameof(TaskItem.UserDone))
                OnPropertyChanged(nameof(TaskBoardSummary));

            // Правка номера трекера у текущей задачи — сразу в маркеры кода
            if (e.PropertyName == nameof(TaskItem.ExternalId) && item.IsCurrent)
                OnCurrentTaskChanged(item);

            if (e.PropertyName != nameof(TaskItem.UserDone)) return;
            if (_suppressUserDoneEcho || IsBusy) return;

            var template = item.UserDone
                ? FindString("str_TaskBoard_UserChecked", "Отметил выполненной задачу «{0}».")
                : FindString("str_TaskBoard_UserUnchecked", "Снял отметку выполнения с задачи «{0}».");

            _ = SendMessageAsync(string.Format(template, item.Name), "free_form");
        }

        // Взводится, пока доску правит сам тул — чтобы правки AI не порождали авто-ходов.
        private bool _suppressUserDoneEcho;

        private Task<ToolResultItem> HandleTaskBoardUpdate(ToolUseDTO toolUse)
        {
            var input = toolUse.Input;
            if (input.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !input.TryGetProperty("tasks", out var tasksEl) ||
                tasksEl.ValueKind != System.Text.Json.JsonValueKind.Array ||
                tasksEl.GetArrayLength() == 0)
            {
                return Task.FromResult(ToolFailure(toolUse, "INVALID_INPUT",
                    "Пустой tasks[]. Каждый элемент — {id, name, status} (status: pending|in_progress|done|blocked|cancelled)."));
            }

            var replace = IsJsonTrue(input, "replace");

            // Разбираем и валидируем ВСЁ до первой мутации доски: иначе ошибка на третьей
            // строке при replace:true оставляла доску очищенной и полузаполненной, а модель
            // получала failure — состояние расходилось с тем, что она думает.
            var parsed = new List<TaskPatch>();
            var index = 0;
            foreach (var el in tasksEl.EnumerateArray())
            {
                index++;

                var id = JsonStr(el, "id");
                if (string.IsNullOrWhiteSpace(id))
                    return Task.FromResult(ToolFailure(toolUse, "INVALID_INPUT",
                        $"Задача №{index}: не передан id."));

                var status = JsonStr(el, "status");
                if (!TaskBoardStatus.IsValid(status))
                    return Task.FromResult(ToolFailure(toolUse, "INVALID_INPUT",
                        $"Задача №{index}: неизвестный status «{status}». Допустимо: pending, in_progress, done, blocked, cancelled."));

                var name = JsonStr(el, "name");
                var isNew = replace || TaskBoard.All(t => t.Id != id);
                if (isNew && string.IsNullOrWhiteSpace(name))
                    return Task.FromResult(ToolFailure(toolUse, "INVALID_INPUT",
                        $"Задача №{index}: новая задача «{id}» без name."));

                parsed.Add(new TaskPatch(id!, name, status!,
                    JsonStr(el, "description"), JsonStr(el, "externalId"), JsonStr(el, "notes")));
            }

            _suppressUserDoneEcho = true;
            try
            {
                if (replace)
                {
                    foreach (var old in TaskBoard) old.PropertyChanged -= OnTaskItemChanged;
                    TaskBoard.Clear();
                }

                var applied = 0;
                foreach (var p in parsed)
                {
                    var existing = TaskBoard.FirstOrDefault(t => t.Id == p.Id);

                    if (existing == null)
                    {
                        var item = new TaskItem
                        {
                            Id = p.Id,
                            Name = p.Name!,
                            Status = p.Status,
                            Description = p.Description,
                            ExternalId = p.ExternalId,
                            Notes = p.Notes
                        };
                        item.PropertyChanged += OnTaskItemChanged;
                        TaskBoard.Add(item);
                    }
                    else
                    {
                        // Имя при обновлении можно не слать — сохраняем прежнее (REQ-021)
                        if (!string.IsNullOrWhiteSpace(p.Name)) existing.Name = p.Name!;
                        existing.Status = p.Status;
                        if (p.Description is { } desc) existing.Description = desc;
                        if (p.ExternalId is { } ext) existing.ExternalId = ext;
                        if (p.Notes is { } notes) existing.Notes = notes;
                    }
                    applied++;
                }

                Renumber();
                EnsureCurrentTask();
                OnPropertyChanged(nameof(TaskBoardSummary));

                // Возвращаем ВСЮ доску, включая UserDone — так AI сразу видит,
                // что пользователь отметил руками, без отдельного тула чтения.
                return Task.FromResult(ToolSuccess(toolUse, new
                {
                    applied,
                    board = TaskBoard.Select(t => new
                    {
                        id = t.Id,
                        name = t.Name,
                        description = t.Description,
                        status = t.Status,
                        userDone = t.UserDone,
                        isCurrent = t.IsCurrent,
                        externalId = t.ExternalId,
                        notes = t.Notes
                    }).ToArray()
                }));
            }
            finally
            {
                _suppressUserDoneEcho = false;
            }
        }

        /// <summary>Разобранная строка тула — до применения к доске.</summary>
        private readonly record struct TaskPatch(
            string Id, string? Name, string Status,
            string? Description, string? ExternalId, string? Notes);

        /// <summary>
        /// Строковое поле из JSON. Модель сплошь и рядом шлёт число там, где схема просит
        /// строку («id»: 1, «externalId»: 902) — принимаем и число тоже, иначе доска
        /// разваливается на первом же ходе AI.
        /// </summary>
        private static string? JsonStr(System.Text.Json.JsonElement el, string prop)
        {
            if (el.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !el.TryGetProperty(prop, out var v)) return null;

            return v.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => v.GetString(),
                System.Text.Json.JsonValueKind.Number => v.GetRawText(),
                _ => null
            };
        }

        /// <summary>Булево поле: принимаем и true, и строку "true" (та же вольность модели).</summary>
        private static bool IsJsonTrue(System.Text.Json.JsonElement el, string prop)
        {
            if (el.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !el.TryGetProperty(prop, out var v)) return false;

            return v.ValueKind == System.Text.Json.JsonValueKind.True ||
                   (v.ValueKind == System.Text.Json.JsonValueKind.String &&
                    string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Сброс доски — новый диалог начинается с чистого листа.</summary>
        protected void ClearTaskBoard()
        {
            foreach (var t in TaskBoard) t.PropertyChanged -= OnTaskItemChanged;
            TaskBoard.Clear();
            IsTaskBoardExpanded = true;
        }

        private static string FindString(string key, string fallback) =>
            System.Windows.Application.Current?.TryFindResource(key) as string ?? fallback;

        /// <summary>
        /// Ручное сжатие контекста (T-000133). Автотриггер срабатывает у самого потолка —
        /// эта кнопка даёт сжать раньше, когда пользователь сам видит, что диалог разросся.
        ///
        /// Сжатие НЕразрушающе: сервер сворачивает старую историю в rolling-summary, но самих
        /// сообщений не трогает — лента у пользователя остаётся прежней. Поэтому в неё
        /// добавляется только строка-checkpoint (её рендер уже есть, T-000130), а не «обрезка».
        ///
        /// Это обычный AI-вызов, и он списывается с баланса — поэтому кнопка, а не автоматика.
        /// </summary>
        private async Task CompactContextAsync()
        {
            if (string.IsNullOrEmpty(SessionId) || IsBusy) return;

            IsBusy = true;
            try
            {
                var r = await Api.CompactSessionAsync(SessionId);

                var text = r.TokensBefore.HasValue && r.TokensAfterEstimate.HasValue
                    ? string.Format(
                        FindString("str_ContextCompactedFmt", "Контекст сжат: {0:N0} → ~{1:N0} токенов"),
                        r.TokensBefore.Value, r.TokensAfterEstimate.Value)
                    : FindString("str_ContextCompacted", "Контекст сжат");

                Messages.Add(new ChatMessage { Role = Models.MessageRoles.Checkpoint, Content = text });
                OnContextCompacted(r);
            }
            catch (ApiException ex) when (ex.ErrorCode == "COMPACTION_BELOW_FLOOR")
            {
                // Не ошибка, а «рано»: сервер бережёт от бессмысленного платного вызова.
                // Показываем спокойной строкой, а не красным ⚠ через OnApiExceptionAsync.
                Messages.Add(new ChatMessage
                {
                    Role = Models.MessageRoles.Checkpoint,
                    Content = FindString("str_CompactBelowFloor",
                        "Сжимать пока нечего — контекст ещё не набрался.")
                });
            }
            catch (ApiException ex)
            {
                await OnApiExceptionAsync(ex);
            }
            catch (Exception ex)
            {
                await OnUnexpectedExceptionAsync(ex);
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Контекст сжали — продукт обновляет своё кольцо занятости. Значение сервера —
        /// ОЦЕНКА: честный замер придёт на следующем AI-ходе.
        /// </summary>
        protected virtual void OnContextCompacted(CompactResponse response) { }

        /// <summary>
        /// Прервать активный chat-ход (submit /chat/async или его polling). Идемпотентно —
        /// повторные клики после Cancel до окончания finally-блока не бросают исключение.
        /// </summary>
        public void Interrupt()
        {
            try { _currentSendCts?.Cancel(); }
            catch (ObjectDisposedException) { /* race с finally SendMessageAsync — ok */ }
        }

        // --- Tool dispatch ---------------------------------------------------

        private static readonly System.Text.Json.JsonSerializerOptions PayloadJson = new()
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>Payload тул-результата → base64-JSON (generic-канал dataB64).</summary>
        protected static string ToDataB64(object payload) =>
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(
                System.Text.Json.JsonSerializer.Serialize(payload, PayloadJson)));

        protected static ToolResultItem ToolSuccess(ToolUseDTO toolUse, object payload) => new()
        {
            ToolUseId = toolUse.Id,
            Name = toolUse.Name,
            Status = Models.ToolResultStatus.Success,
            DataB64 = ToDataB64(payload)
        };

        protected static ToolResultItem ToolFailure(ToolUseDTO toolUse, string error, string message, object? payload = null) => new()
        {
            ToolUseId = toolUse.Id,
            Name = toolUse.Name,
            Status = Models.ToolResultStatus.Failure,
            Message = message,
            DataB64 = ToDataB64(payload ?? new { error, message })
        };

        /// <summary>
        /// Зарегистрировать handler для tool_use по имени. Продуктовые наследники
        /// вызывают это в конструкторе для legacy (apply_code_change / execute_command)
        /// и продуктовых тулов (1С: onec_*; Universal: только legacy пока).
        /// Повторная регистрация под тем же именем перекрывает предыдущую.
        /// </summary>
        /// <param name="declare">
        /// Объявлять ли тул серверу в <see cref="ClientTools"/> (F-000042 / REQ-057, T-000156).
        /// «Умею обработать вызов» и «умею сделать работу» — РАЗНЫЕ вещи: заглушка обработает
        /// вызов и честно вернёт not_implemented, но объявлять её нельзя — модель позовёт её и
        /// сожжёт ход впустую. Handler у заглушки остаётся: старый сервер может задекларировать
        /// тул сам, и ответить «не умею» вежливее, чем «no handler».
        /// </param>
        protected void RegisterToolHandler(string name, Func<ToolUseDTO, Task<ToolResultItem>> handler,
            bool declare = true)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("Tool name is required", nameof(name));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _toolHandlers[name] = handler;
            if (declare) _declaredTools.Add(name);
            else _declaredTools.Remove(name);
        }

        private readonly SortedSet<string> _declaredTools = new(StringComparer.Ordinal);

        /// <summary>
        /// Имена client-executed тулов, которые клиент РЕАЛЬНО умеет исполнить (F-000042 / REQ-057).
        /// Едут в каждом запросе хода: сервер пересекает свой каталог продукта с этим набором.
        ///
        /// Зачем: до этого состав тулов решал ОДИН сервер и не знал, какой версии клиент на том
        /// конце. 13.07 это выстрелило в бою — сервер объявил модели read_oneC_procedure, клиент
        /// его ещё не умел, модель звала объявленный ей тул, получала «No handler registered» и,
        /// не найдя способа заменить процедуру целиком, шла портить модуль мелкими правками.
        /// Теперь состав тулов — пересечение того, что сервер даёт, и того, что клиент умеет.
        /// </summary>
        public string[] ClientTools => _declaredTools.ToArray();

        /// <summary>
        /// Диспатчит массив tool_use'ов из ответа сервера: для каждого зовёт зарегистрированный
        /// handler, собирает ToolResultItem'ы, шлёт обратно через PostToolResultsAsync.
        /// Незарегистрированные тулы возвращают failure-результат «no handler» — сервер увидит
        /// и передаст AI, тот пробует другой подход.
        /// </summary>
        protected async Task DispatchToolUsesAsync(IReadOnlyList<ToolUseDTO> toolUses)
        {
            if (toolUses == null || toolUses.Count == 0) return;

            var results = new List<ToolResultItem>(toolUses.Count);
            foreach (var toolUse in toolUses)
            {
                var call = FindToolCall(toolUse.Id);
                ToolResultItem result;
                if (_toolHandlers.TryGetValue(toolUse.Name, out var handler))
                {
                    try
                    {
                        result = await handler(toolUse);
                        result.ToolUseId = toolUse.Id;
                        result.Name ??= toolUse.Name;
                    }
                    catch (Exception ex)
                    {
                        result = new ToolResultItem
                        {
                            ToolUseId = toolUse.Id,
                            Name = toolUse.Name,
                            Status = Models.ToolResultStatus.Failure,
                            Message = $"Handler threw: {ex.Message}"
                        };
                    }
                }
                else
                {
                    result = new ToolResultItem
                    {
                        ToolUseId = toolUse.Id,
                        Name = toolUse.Name,
                        Status = Models.ToolResultStatus.Failure,
                        Message = $"No handler registered for tool '{toolUse.Name}'"
                    };
                }
                results.Add(result);

                // Живой статус карточки тула (T-000074)
                if (call != null)
                {
                    call.Status = result.Status == Models.ToolResultStatus.Failure
                        ? ToolCallItem.StatusFailed
                        : ToolCallItem.StatusDone;
                    call.ResultMessage = result.Message;
                }
            }

            await PostToolResultsAsync(results);
        }

        private ToolCallItem? FindToolCall(string toolUseId)
        {
            var msg = _activeToolCallsMessage;
            if (msg == null) return null;
            foreach (var c in msg.ToolCalls)
                if (c.ToolUseId == toolUseId) return c;
            return null;
        }

        /// <summary>
        /// Отправка tool-результатов обратно на сервер. Реализовано T-000047.
        /// Сервер отвечает следующим assistant-ходом (retry-response), который может
        /// содержать новые toolUses[] — тогда пайплайн рекурсивно диспатчит их дальше.
        /// Наследник может переопределить для локального логирования / debug bubble'ов.
        /// </summary>
        /// <summary>
        /// Режим ассистента (planning | auto | review), если продукт их знает. База — null:
        /// у Universal режимов нет, и поле в запрос не попадёт. 1С-клиент переопределяет
        /// (T-000095) — иначе «Ревью» действовал бы только на первом запросе хода, а правки
        /// модель делает в продолжениях.
        /// </summary>
        protected virtual string? AssistMode => null;

        protected virtual async Task PostToolResultsAsync(IReadOnlyList<ToolResultItem> results)
        {
            if (string.IsNullOrEmpty(SessionId) || results.Count == 0) return;

            BeginToolContinuation();
            try
            {
                var retry = await Api.PostToolResultsAsync(SessionId, results, AssistMode, ClientTools);
                if (!string.IsNullOrEmpty(retry.SessionId))
                    SessionId = retry.SessionId;

                EndToolContinuation();
                await HandleToolRetryResponseAsync(retry);
            }
            catch (ApiException ex)
            {
                EndToolContinuation();
                await OnApiExceptionAsync(ex);
            }
            catch (Exception ex)
            {
                EndToolContinuation();
                await OnUnexpectedExceptionAsync(ex);
            }
        }

        /// <summary>
        /// Индикатор ожидания на round-trip'е tool-результатов: без него после срабатывания
        /// тула диалог выглядит замершим, хотя ход ещё не закончен.
        ///
        /// По умолчанию — своя транзиентная строка. Наследник, у которого УЖЕ есть свой
        /// индикатор хода (ротация фраз «Думаю…»), переопределяет пару и переиспользует его:
        /// продолжение после тула — это тот же ход, и выглядеть оно должно так же. Отдельная
        /// строка «Инструмент отработал — обрабатываю результат» только шумела: что инструмент
        /// отработал, и так видно по его карточке.
        /// </summary>
        protected virtual void BeginToolContinuation()
        {
            _toolWaiting = new ChatMessage
            {
                Role = MessageRoles.Assistant,
                IsThinking = true,
                Content = System.Windows.Application.Current?.TryFindResource("str_Chat_Thinking") as string
                          ?? "думаю…"
            };
            Messages.Add(_toolWaiting);
        }

        /// <summary>Снять индикатор ожидания. Обязан быть идемпотентным.</summary>
        protected virtual void EndToolContinuation()
        {
            if (_toolWaiting == null) return;
            Messages.Remove(_toolWaiting);
            _toolWaiting = null;
        }

        private ChatMessage? _toolWaiting;

        /// <summary>
        /// Обработка ответа /chat/tool-results — по сути следующий assistant-ход
        /// с retry-статой (AttemptsUsed/Max/Exhausted). Наследник переопределяет
        /// для legacy channels (codeChanges/executeCommands) и для UI-индикации retry.
        /// </summary>
        protected virtual async Task HandleToolRetryResponseAsync(ChatToolRetryResponse retry)
        {
            var msg = BuildAssistantMessage(
                content: retry.Content,
                model: retry.Model,
                modelDisplay: retry.ModelDisplayName,
                promptTokens: retry.PromptTokens,
                completionTokens: retry.CompletionTokens,
                totalTokens: retry.TotalTokens,
                reasoningText: retry.ReasoningText,
                reasoningEffort: retry.ReasoningEffort,
                balanceCoins: retry.BalanceCoins,
                costCoins: retry.CostCoins,
                costUsd: retry.CostUsd,
                costRub: retry.CostRub,
                userMessageId: null,
                cachedTokens: retry.CachedTokens ?? 0,
                toolUses: retry.ToolUses,
                hasLegacyCodeChanges: retry.CodeChanges is { Length: > 0 });
            Messages.Add(msg);
            AttachToolCalls(msg);
            ChainAccumulate(msg, isFinal: retry.ToolUses is not { Length: > 0 } || retry.RetryExhausted);

            if (retry.RetryExhausted)
            {
                Messages.Add(new ChatMessage
                {
                    Role = MessageRoles.Debug,
                    Content = $"Retry исчерпан: {retry.AttemptsUsed}/{retry.AttemptsMax}"
                });
                return;
            }

            await ProcessToolRetryLegacyChannelsAsync(retry);

            if (retry.ToolUses is { Length: > 0 })
                await DispatchToolUsesAsync(retry.ToolUses);
        }

        /// <summary>Hook для наследника — legacy channels в tool-retry response.</summary>
        protected virtual Task ProcessToolRetryLegacyChannelsAsync(ChatToolRetryResponse retry) => Task.CompletedTask;

        // --- Send / receive pipeline ----------------------------------------

        /// <summary>
        /// Собрать ChatRequest с продуктовым контекстом (ProjectSettings, framework, ide и т.д.).
        /// Каждый наследник знает свой контекст — базе не место.
        /// </summary>
        protected abstract ChatRequest BuildRequest(string message, string promptCode);

        /// <summary>
        /// Публичный API для наследника — отправить сообщение с промпт-кодом.
        /// Добавляет user-message в коллекцию, ставит IsBusy, зовёт API, обрабатывает ответ.
        /// </summary>
        public async Task SendMessageAsync(string message, string promptCode = "free_form")
        {
            if (string.IsNullOrWhiteSpace(message) || IsBusy) return;

            Messages.Add(new ChatMessage { Role = MessageRoles.User, Content = message });
            InputText = string.Empty;
            IsBusy = true;
            ChainStart();

            var cts = new CancellationTokenSource();
            _currentSendCts = cts;

            try
            {
                // Доска переехала в новый диалог — модель о ней ещё НЕ знает: в контекст запроса
                // доска не попадает, AI узнаёт о ней только из своих же вызовов task_board_update.
                // Без этой врезки перенос был бы чисто визуальным: человек видит план, а модель
                // начинает с чистого листа и перепланирует заново (T-000159).
                // Врезаем в ПЕРВОЕ сообщение нового диалога, а не отдельным ходом: лишний ход —
                // это лишние деньги, а весь смысл переноса в том, чтобы холодный старт был дешёвым.
                var outgoing = message;
                if (_boardCarriedOver && TaskBoard.Count > 0)
                {
                    outgoing = BuildBoardHandoff() + "\n\n" + message;
                    _boardCarriedOver = false;
                }

                var request = BuildRequest(outgoing, promptCode);
                var response = await Api.SendChatAsyncAsync(request, cancellationToken: cts.Token);

                if (!string.IsNullOrEmpty(response.SessionId))
                    SessionId = response.SessionId;

                await HandleChatResponseAsync(response);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                await OnChatInterruptedAsync();
            }
            catch (ApiException ex)
            {
                await OnApiExceptionAsync(ex);
            }
            catch (Exception ex)
            {
                await OnUnexpectedExceptionAsync(ex);
            }
            finally
            {
                _currentSendCts = null;
                cts.Dispose();
                IsBusy = false;
            }
        }

        /// <summary>
        /// Наследник может переопределить для локализации / особого UI. Default —
        /// добавляет системное сообщение о прерывании. Билинг hold сервер отпустит
        /// сам по istekшему TTL (abandoned) либо по завершению уже запущенного хода.
        /// </summary>
        protected virtual Task OnChatInterruptedAsync()
        {
            Messages.Add(new ChatMessage
            {
                Role = MessageRoles.Debug,
                Content = "⏹ Прервано пользователем"
            });
            return Task.CompletedTask;
        }

        /// <summary>
        /// Обработка ответа /chat: добавить assistant-message со всеми полями (reasoning,
        /// billing, tokens, toolUses), диспатчить toolUses[], hook на legacy-каналы.
        /// Виртуальный — наследник может переопределить если нужен полный контроль
        /// (например, переиспользование thinking-bubble вместо новой строки).
        /// </summary>
        protected virtual async Task HandleChatResponseAsync(ChatResponse response)
        {
            var msg = BuildAssistantMessage(
                content: response.Content,
                model: response.Model,
                modelDisplay: response.ModelDisplayName,
                promptTokens: response.PromptTokens,
                completionTokens: response.CompletionTokens,
                totalTokens: response.TotalTokens,
                reasoningText: response.ReasoningText,
                reasoningEffort: response.ReasoningEffort,
                balanceCoins: response.BalanceCoins,
                costCoins: response.CostCoins,
                costUsd: response.CostUsd,
                costRub: response.CostRub,
                userMessageId: response.UserMessageId,
                cachedTokens: response.CachedTokens ?? 0,
                toolUses: response.ToolUses,
                hasLegacyCodeChanges: response.CodeChanges is { Length: > 0 });
            Messages.Add(msg);
            AttachToolCalls(msg);
            ChainAccumulate(msg, isFinal: response.ToolUses is not { Length: > 0 });

            await ProcessLegacyChannelsAsync(response);

            if (response.ToolUses is { Length: > 0 })
                await DispatchToolUsesAsync(response.ToolUses);
        }

        /// <summary>
        /// Собрать ChatMessage assistant-роли из полей ChatResponse / ChatToolRetryResponse.
        /// Дедуп между HandleChatResponseAsync и HandleToolRetryResponseAsync.
        /// </summary>
        private static ChatMessage BuildAssistantMessage(
            string content,
            string model,
            string modelDisplay,
            int promptTokens,
            int completionTokens,
            int totalTokens,
            string? reasoningText,
            string? reasoningEffort,
            decimal? balanceCoins,
            decimal? costCoins,
            decimal? costUsd,
            decimal? costRub,
            int? userMessageId,
            int cachedTokens,
            ToolUseDTO[]? toolUses,
            bool hasLegacyCodeChanges)
        {
            var msg = new ChatMessage
            {
                Role = MessageRoles.Assistant,
                Content = content,
                ModelName = !string.IsNullOrEmpty(modelDisplay) ? modelDisplay : model,
                PromptTokens = promptTokens,
                CachedTokens = cachedTokens,
                CompletionTokens = completionTokens,
                TotalTokens = totalTokens,
                ReasoningText = reasoningText,
                ReasoningEffort = reasoningEffort,
                BalanceCoins = balanceCoins,
                CostCoins = costCoins,
                CostUsd = costUsd,
                CostRub = costRub,
                UserMessageId = userMessageId,
                HasCodeChanges = hasLegacyCodeChanges
            };
            if (toolUses is { Length: > 0 })
                msg.ToolUses = new List<ToolUseDTO>(toolUses);
            return msg;
        }

        /// <summary>
        /// Hook для наследника — обработка legacy-каналов ChatResponse:
        /// codeChanges[] и executeCommands[]. Наследник переопределяет и собирает
        /// ToolResultItem[] для tool-retry цикла.
        /// </summary>
        protected virtual Task ProcessLegacyChannelsAsync(ChatResponse response) => Task.CompletedTask;

        // --- Error hooks -----------------------------------------------------

        /// <summary>
        /// Обработка ApiException. Наследник переопределяет для локализации сообщений
        /// и специальных action'ов (например, RequestLogout на UNAUTHORIZED).
        /// Default: добавить assistant-сообщение с текстом ошибки.
        /// </summary>
        protected virtual Task OnApiExceptionAsync(ApiException ex)
        {
            Messages.Add(new ChatMessage
            {
                Role = MessageRoles.Assistant,
                Content = $"⚠ {ex.ErrorCode}: {ex.Message}"
            });
            return Task.CompletedTask;
        }

        protected virtual Task OnUnexpectedExceptionAsync(Exception ex)
        {
            Messages.Add(new ChatMessage
            {
                Role = MessageRoles.Assistant,
                Content = $"⚠ {ex.Message}"
            });
            return Task.CompletedTask;
        }

        // --- Streaming hooks (T-000067 задел) --------------------------------

        /// <summary>
        /// Заготовка под SSE-стриминг. В T-000067 (v0.11.4) заменится реальным
        /// SendChatAsyncAsync + polling. Пока не вызывается.
        /// </summary>
        protected virtual Task OnStreamChunkAsync(ChatMessage assistantMessage, string delta) => Task.CompletedTask;

        protected virtual Task OnStreamCompletedAsync(ChatMessage assistantMessage) => Task.CompletedTask;

        // --- New chat --------------------------------------------------------

        protected virtual void NewChat()
        {
            Messages.Clear();
            SessionId = string.Empty;
            _boardCarriedOver = false;
            ClearTaskBoard();
        }

        /// <summary>
        /// Новый диалог, но доска переезжает с собой (T-000159).
        ///
        /// Зачем: новый диалог — единственная дешёвая операция в экономике кэша. Кэш промпта
        /// живёт для КОНКРЕТНОЙ модели и КОНКРЕТНОГО префикса истории, поэтому и смена модели,
        /// и сжатие контекста его обнуляют. Начать с чистого листа дешевле всего — но до сих пор
        /// это стоило потери доски, и потому им никто не пользовался. Теперь план едет следом,
        /// и «начать заново» перестаёт быть потерей работы.
        /// </summary>
        protected virtual void NewChatWithBoard()
        {
            var carried = TaskBoard.Count > 0;
            Messages.Clear();
            SessionId = string.Empty;
            _boardCarriedOver = carried;
            // Доску НЕ чистим: она уляжется под новый sessionId, как только сервер его выдаст
            // (см. сеттер SessionId — там уже есть перенос черновика).
        }

        // Доска переехала, но модель ещё не знает — врезка уйдёт в первое сообщение.
        private bool _boardCarriedOver;

        /// <summary>
        /// Доска текстом — для модели, в первое сообщение перенесённого диалога. Компактно:
        /// на большом модуле каждый токен входа платный, а доска нужна как ориентир, не как
        /// протокол. Формат тот же, каким модель сама её и наполняет через task_board_update.
        /// </summary>
        private string BuildBoardHandoff()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Продолжаем работу по уже составленному плану. Доска задач на текущий момент:");

            foreach (var t in TaskBoard)
            {
                sb.Append("- id=").Append(t.Id).Append(" | ").Append(t.Name);
                sb.Append(" | статус: ").Append(t.Status);
                if (t.UserDone) sb.Append(" | человек отметил выполненной");
                if (t.IsCurrent) sb.Append(" | ТЕКУЩАЯ");
                if (!string.IsNullOrWhiteSpace(t.ExternalId)) sb.Append(" | трекер: ").Append(t.ExternalId);
                if (!string.IsNullOrWhiteSpace(t.Description)) sb.Append(" | ").Append(t.Description);
                sb.AppendLine();
            }

            sb.Append("Доска уже заполнена — заново её не составляй. Обновляй существующие строки " +
                      "через task_board_update по тем же id.");
            return sb.ToString();
        }
    }
}
