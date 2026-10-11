using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
        private static Dictionary<string, JsonElement> _unknownRecords = new(StringComparer.Ordinal);

        private static string V2FilePath =>
            Path.Combine(AppConfigService.ConfigDirectory, "task-boards.v2.json");

        private static string LegacyFilePath =>
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

        /// <summary>Known snapshots plus raw records this client cannot interpret.</summary>
        public sealed record ParsedV2(
            IReadOnlyList<BoardSnapshot> Boards,
            IReadOnlyDictionary<string, JsonElement> UnknownRecords);

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

        /// <summary>Сериализовать коллекцию снимков в формат task-boards.v2.json.</summary>
        public static ParsedV2 ParseV2(
            string json,
            DateTimeOffset fallbackUpdatedAt,
            Func<string, string?>? titleResolver = null)
        {
            var boards = new List<BoardSnapshot>();
            var unknown = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(json))
                return new ParsedV2(boards.AsReadOnly(), unknown);

            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return new ParsedV2(boards.AsReadOnly(), unknown);

                foreach (var property in document.RootElement.EnumerateObject())
                {
                    try
                    {
                        var value = property.Value;
                        if (value.ValueKind == JsonValueKind.Array)
                        {
                            var legacyTasks = DeserializeTasks(value);
                            if (legacyTasks is null)
                                unknown[property.Name] = value.Clone();
                            else
                                boards.Add(CreateSnapshot(property.Name, legacyTasks,
                                    titleResolver?.Invoke(property.Name), fallbackUpdatedAt));
                            continue;
                        }

                        if (value.ValueKind != JsonValueKind.Object ||
                            !TryGetProperty(value, "tasks", out var tasksElement) ||
                            tasksElement.ValueKind != JsonValueKind.Array)
                        {
                            unknown[property.Name] = value.Clone();
                            continue;
                        }

                        var tasks = DeserializeTasks(tasksElement);
                        if (tasks is null)
                        {
                            unknown[property.Name] = value.Clone();
                            continue;
                        }

                        string? title = null;
                        if (TryGetProperty(value, "title", out var titleElement) &&
                            titleElement.ValueKind == JsonValueKind.String)
                            title = titleElement.GetString();
                        var updatedAt = fallbackUpdatedAt;
                        if (TryGetProperty(value, "updatedAt", out var dateElement) &&
                            dateElement.ValueKind == JsonValueKind.String &&
                            dateElement.TryGetDateTimeOffset(out var storedDate))
                            updatedAt = storedDate;
                        if (string.IsNullOrWhiteSpace(title)) title = titleResolver?.Invoke(property.Name);
                        boards.Add(CreateSnapshot(property.Name, tasks, title, updatedAt));
                    }
                    catch
                    {
                        unknown[property.Name] = property.Value.Clone();
                    }
                }
            }
            catch
            {
                // An invalid root has no safely recoverable per-record structure.
            }

            return new ParsedV2(boards.AsReadOnly(), unknown);
        }

        public static string Serialize(IEnumerable<BoardSnapshot> snapshots) => SerializeV2(snapshots, null);

        public static string SerializeV2(
            IEnumerable<BoardSnapshot> snapshots,
            IReadOnlyDictionary<string, JsonElement>? unknownRecords)
        {
            ArgumentNullException.ThrowIfNull(snapshots);
            var map = new Dictionary<string, BoardSnapshot>(StringComparer.Ordinal);
            foreach (var snapshot in snapshots)
            {
                if (snapshot == null || string.IsNullOrEmpty(snapshot.SessionId)) continue;
                map[snapshot.SessionId] = CreateSnapshot(
                    snapshot.SessionId, snapshot.Tasks, snapshot.Title, snapshot.UpdatedAt);
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                foreach (var snapshot in map.Values)
                {
                    writer.WritePropertyName(snapshot.SessionId);
                    JsonSerializer.Serialize(writer, new
                    {
                        tasks = snapshot.Tasks,
                        title = snapshot.Title,
                        updatedAt = snapshot.UpdatedAt
                    }, JsonOptions);
                }
                if (unknownRecords is not null)
                {
                    foreach (var (sessionId, raw) in unknownRecords)
                    {
                        if (map.ContainsKey(sessionId)) continue;
                        writer.WritePropertyName(sessionId);
                        raw.WriteTo(writer);
                    }
                }
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
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
                if (File.Exists(V2FilePath))
                {
                    var info = new FileInfo(V2FilePath);
                    var fallbackUpdatedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                    var parsed = ParseV2(File.ReadAllText(V2FilePath), fallbackUpdatedAt);
                    _unknownRecords = parsed.UnknownRecords.ToDictionary(
                        pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
                    _map = parsed.Boards.ToDictionary(snapshot => snapshot.SessionId, StringComparer.Ordinal);
                }
                else if (File.Exists(LegacyFilePath))
                {
                    var info = new FileInfo(LegacyFilePath);
                    var legacyUpdatedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
                    var parsed = ParseV2(File.ReadAllText(LegacyFilePath), legacyUpdatedAt);
                    _unknownRecords = parsed.UnknownRecords.ToDictionary(
                        pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
                    _map = parsed.Boards.ToDictionary(snapshot => snapshot.SessionId, StringComparer.Ordinal);
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
                WriteAtomically(V2FilePath, SerializeV2(map.Values, _unknownRecords));
            }
            catch { /* доска — вспомогательные данные, сбой записи не роняет чат */ }
        }

        private static void WriteAtomically(string path, string contents)
        {
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporaryPath, contents, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporaryPath, path, null);
                else File.Move(temporaryPath, path);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
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
