using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LenSa.Core.Chat.Models;
using LenSa.Core.Services.AppConfig;

namespace LenSa.Core.Chat.Services
{
    /// <summary>
    /// Локальное хранилище досок задач (T-000131): sessionId → строки доски.
    ///
    /// Доска НЕ хранится на сервере — так решено в REQ-021 сознательно: это личный план
    /// человека по своей базе, он должен жить и без сети. Значит хранить обязан клиент,
    /// иначе доска умирает при перезапуске (смок 12.07).
    ///
    /// Ключ — sessionId диалога, тем же измерением, что и привязка диалога к базе
    /// (DialogBindingService). Файл лежит рядом с остальными настройками продукта и
    /// живёт на одной машине.
    /// </summary>
    public static class TaskBoardStore
    {
        private static readonly object Sync = new();
        private static Dictionary<string, List<TaskDto>>? _map;

        private static string FilePath =>
            Path.Combine(AppConfigService.ConfigDirectory, "task-boards.json");

        /// <summary>Строка доски в том виде, в каком лежит на диске.</summary>
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

        /// <summary>Доска сессии; пустой список — доски нет.</summary>
        public static List<TaskItem> Load(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return new List<TaskItem>();

            lock (Sync)
            {
                if (!LoadMap().TryGetValue(sessionId, out var dtos)) return new List<TaskItem>();

                return dtos.Select(d => new TaskItem
                {
                    Id = d.Id,
                    Name = d.Name,
                    Status = TaskBoardStatus.IsValid(d.Status) ? d.Status : TaskBoardStatus.Pending,
                    Description = d.Description,
                    ExternalId = d.ExternalId,
                    Notes = d.Notes,
                    UserDone = d.UserDone,
                    IsCurrent = d.IsCurrent
                }).ToList();
            }
        }

        /// <summary>Сохранить доску сессии. Пустая доска — запись удаляется.</summary>
        public static void Save(string sessionId, IEnumerable<TaskItem> tasks)
        {
            if (string.IsNullOrEmpty(sessionId)) return;

            lock (Sync)
            {
                var map = LoadMap();
                var list = tasks.Select(t => new TaskDto
                {
                    Id = t.Id,
                    Name = t.Name,
                    Status = t.Status,
                    Description = t.Description,
                    ExternalId = t.ExternalId,
                    Notes = t.Notes,
                    UserDone = t.UserDone,
                    IsCurrent = t.IsCurrent
                }).ToList();

                if (list.Count == 0)
                {
                    if (!map.Remove(sessionId)) return;
                }
                else
                {
                    map[sessionId] = list;
                }

                SaveMap(map);
            }
        }

        /// <summary>
        /// Чистка досок мёртвых сессий — сервер отдал живой список.
        /// Та же семантика, что у DialogBindingService.PruneExcept.
        /// </summary>
        public static void PruneExcept(IEnumerable<string> liveSessionIds)
        {
            lock (Sync)
            {
                var live = new HashSet<string>(liveSessionIds);
                var map = LoadMap();
                var dead = map.Keys.Where(k => !live.Contains(k)).ToList();
                if (dead.Count == 0) return;

                foreach (var k in dead) map.Remove(k);
                SaveMap(map);
            }
        }

        private static Dictionary<string, List<TaskDto>> LoadMap()
        {
            if (_map != null) return _map;
            try
            {
                if (File.Exists(FilePath))
                    _map = JsonSerializer.Deserialize<Dictionary<string, List<TaskDto>>>(
                        File.ReadAllText(FilePath));
            }
            catch { /* битый файл — начинаем с пустой карты */ }
            return _map ??= new Dictionary<string, List<TaskDto>>();
        }

        private static void SaveMap(Dictionary<string, List<TaskDto>> map)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath,
                    JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* доска — вспомогательные данные, сбой записи не роняет чат */ }
        }
    }
}
