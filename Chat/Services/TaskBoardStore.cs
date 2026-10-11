using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using LenSa.Core.Chat.Models;
using LenSa.Core.Services.AppConfig;

namespace LenSa.Core.Chat.Services
{
    /// <summary>
    /// Локальное хранилище досок задач: sessionId → снимок доски. Данные остаются
    /// локальными; заголовок передаёт продукт через API, Core не ищет сведения о сессиях.
    /// </summary>
    public static class TaskBoardStore
    {
        private static readonly object Sync = new();
        private static Dictionary<string, BoardSnapshot>? _map;
        private static DateTimeOffset? _legacyFileUpdatedAt;

        private static string FilePath =>
            Path.Combine(AppConfigService.ConfigDirectory, "task-boards.json");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>Строка доски в сериализованном снимке.</summary>
        public sealed class TaskDto
        {
            public string Id { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public string Status { get; set; } = TaskBoardStatus.Pending;
            public string? Description { get; set; }
            public string? ExternalId { get; set; }
            public string? Notes { get; set; }
            public bool UserDone { get; set; }
            public bool IsCurrent { get; set; }
        }

        /// <summary>Неизменяемая оболочка снимка; задачи копируются при создании и выдаче.</summary>
        public sealed record BoardSnapshot(
            string SessionId,
            IReadOnlyList<TaskDto> Tasks,
            string? Title,
            DateTimeOffset UpdatedAt);

        /// <summary>Создать независимую копию снимка из задач интерфейса.</summary>
        public static BoardSnapshot Snapshot(
            string sessionId,
            IEnumerable<TaskItem> tasks,
            string? title,
            DateTimeOffset updatedAt)
        {
            ArgumentNullException.ThrowIfNull(tasks);
            return CreateSnapshot(sessionId, tasks.Select(ToDto), title, updatedAt);
        }

        /// <summary>
        /// Чисто разобрать старый массив задач или новый объект-снимок. Ошибка одной записи
        /// пропускает только её; повреждение корневого JSON возвращает пустой список.
        /// </summary>
        public static IReadOnlyList<BoardSnapshot> Parse(
            string json,
            DateTimeOffset legacyUpdatedAt,
            Func<string, string?>? titleResolver = null)
        {
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<BoardSnapshot>();

            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return Array.Empty<BoardSnapshot>();

                var result = new List<BoardSnapshot>();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    try
                    {
                        IReadOnlyList<TaskDto>? tasks;
                        string? title = null;
                        var updatedAt = legacyUpdatedAt;

                        if (property.Value.ValueKind == JsonValueKind.Array)
                        {
                            tasks = DeserializeTasks(property.Value);
                        }
                        else if (property.Value.ValueKind == JsonValueKind.Object)
                        {
                            if (!TryGetProperty(property.Value, "tasks", out var tasksElement) ||
                                tasksElement.ValueKind != JsonValueKind.Array)
                                continue;

                            tasks = DeserializeTasks(tasksElement);
                            if (TryGetProperty(property.Value, "title", out var titleElement) &&
                                titleElement.ValueKind == JsonValueKind.String)
                                title = titleElement.GetString();
                            if (TryGetProperty(property.Value, "updatedAt", out var dateElement) &&
                                dateElement.ValueKind == JsonValueKind.String &&
                                dateElement.TryGetDateTimeOffset(out var storedDate))
                                updatedAt = storedDate;
                        }
                        else
                        {
                            continue;
                        }

                        if (tasks == null) continue;
                        if (string.IsNullOrWhiteSpace(title))
                            title = titleResolver?.Invoke(property.Name);
                        result.Add(CreateSnapshot(property.Name, tasks, title, updatedAt));
                    }
                    catch
                    {
                        // Запись неизвестного/повреждённого формата не должна скрыть соседние доски.
                    }
                }

                return result.AsReadOnly();
            }
            catch
            {
                return Array.Empty<BoardSnapshot>();
            }
        }

        /// <summary>Сериализовать коллекцию снимков в формат task-boards.json.</summary>
        public static string Serialize(IEnumerable<BoardSnapshot> snapshots)
        {
            ArgumentNullException.ThrowIfNull(snapshots);
            var map = new Dictionary<string, BoardSnapshot>(StringComparer.Ordinal);
            foreach (var snapshot in snapshots)
            {
                if (snapshot == null || string.IsNullOrEmpty(snapshot.SessionId)) continue;
                map[snapshot.SessionId] = CreateSnapshot(
                    snapshot.SessionId, snapshot.Tasks, snapshot.Title, snapshot.UpdatedAt);
            }
            return JsonSerializer.Serialize(map, JsonOptions);
        }

        /// <summary>Снимки всех досок; legacy-заголовок разрешается продуктовым callback.</summary>
        public static IReadOnlyList<BoardSnapshot> LoadSnapshots(
            Func<string, string?>? titleResolver = null)
        {
            lock (Sync)
            {
                var map = LoadMap();
                var result = map.Values.Select(snapshot =>
                {
                    var title = snapshot.Title;
                    if (string.IsNullOrWhiteSpace(title))
                        title = titleResolver?.Invoke(snapshot.SessionId);
                    return CreateSnapshot(snapshot.SessionId, snapshot.Tasks, title, snapshot.UpdatedAt);
                }).ToList();
                return result.AsReadOnly();
            }
        }

        /// <summary>Загрузить задачи сессии; legacy API сохранён.</summary>
        public static List<TaskItem> Load(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return new List<TaskItem>();

            lock (Sync)
            {
                if (!LoadMap().TryGetValue(sessionId, out var snapshot)) return new List<TaskItem>();
                return snapshot.Tasks.Select(ToTaskItem).ToList();
            }
        }

        /// <summary>Сохранить доску сессии. Пустая доска удаляет запись.</summary>
        public static void Save(string sessionId, IEnumerable<TaskItem> tasks) =>
            Save(sessionId, tasks, null);

        /// <summary>Сохранить доску и продуктовый заголовок; пустой заголовок сохраняет прежний.</summary>
        public static void Save(string sessionId, IEnumerable<TaskItem> tasks, string? title)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            ArgumentNullException.ThrowIfNull(tasks);

            lock (Sync)
            {
                var map = LoadMap();
                var items = tasks.Select(ToDto).ToList();
                if (items.Count == 0)
                {
                    if (!map.Remove(sessionId)) return;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(title) && map.TryGetValue(sessionId, out var previous))
                        title = previous.Title;
                    map[sessionId] = CreateSnapshot(
                        sessionId, items, title, DateTimeOffset.UtcNow);
                }

                SaveMap(map);
            }
        }

        /// <summary>Удалить доски удалённых сессий, сохраняя снимки живых без изменений.</summary>
        public static void PruneExcept(IEnumerable<string> liveSessionIds)
        {
            ArgumentNullException.ThrowIfNull(liveSessionIds);
            lock (Sync)
            {
                var live = new HashSet<string>(liveSessionIds, StringComparer.Ordinal);
                var map = LoadMap();
                var dead = map.Keys.Where(k => !live.Contains(k)).ToList();
                if (dead.Count == 0) return;
                foreach (var key in dead) map.Remove(key);
                SaveMap(map);
            }
        }

        private static Dictionary<string, BoardSnapshot> LoadMap()
        {
            if (_map != null) return _map;
            try
            {
                if (File.Exists(FilePath))
                {
                    var info = new FileInfo(FilePath);
                    _legacyFileUpdatedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                    _map = Parse(File.ReadAllText(FilePath), _legacyFileUpdatedAt.Value)
                        .ToDictionary(snapshot => snapshot.SessionId, StringComparer.Ordinal);
                }
            }
            catch
            {
                // Ошибка файла/одной записи не должна останавливать чат.
            }
            return _map ??= new Dictionary<string, BoardSnapshot>(StringComparer.Ordinal);
        }

        private static void SaveMap(Dictionary<string, BoardSnapshot> map)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, Serialize(map.Values));
            }
            catch { /* доска — вспомогательные данные, сбой записи не роняет чат */ }
        }

        private static IReadOnlyList<TaskDto>? DeserializeTasks(JsonElement element) =>
            JsonSerializer.Deserialize<List<TaskDto>>(element.GetRawText(), JsonOptions)?
                .Select(CloneDto).ToList().AsReadOnly();

        private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
            value = default;
            return false;
        }

        private static BoardSnapshot CreateSnapshot(
            string sessionId,
            IEnumerable<TaskDto> tasks,
            string? title,
            DateTimeOffset updatedAt)
        {
            var copies = tasks.Select(CloneDto).ToList().AsReadOnly();
            return new BoardSnapshot(sessionId, copies, title, updatedAt);
        }

        private static TaskDto ToDto(TaskItem task) => new()
        {
            Id = task.Id,
            Name = task.Name,
            Status = task.Status,
            Description = task.Description,
            ExternalId = task.ExternalId,
            Notes = task.Notes,
            UserDone = task.UserDone,
            IsCurrent = task.IsCurrent
        };

        private static TaskDto CloneDto(TaskDto task) => new()
        {
            Id = task.Id,
            Name = task.Name,
            Status = task.Status,
            Description = task.Description,
            ExternalId = task.ExternalId,
            Notes = task.Notes,
            UserDone = task.UserDone,
            IsCurrent = task.IsCurrent
        };

        private static TaskItem ToTaskItem(TaskDto task) => new()
        {
            Id = task.Id,
            Name = task.Name,
            Status = TaskBoardStatus.IsValid(task.Status) ? task.Status : TaskBoardStatus.Pending,
            Description = task.Description,
            ExternalId = task.ExternalId,
            Notes = task.Notes,
            UserDone = task.UserDone,
            IsCurrent = task.IsCurrent
        };
    }
}
