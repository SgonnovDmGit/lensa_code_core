using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LenSa.Core.Services.AppConfig
{
    public class AppConfig
    {
        [JsonPropertyName("theme")]
        public string Theme { get; set; } = "Light";

        [JsonPropertyName("language")]
        public string Language { get; set; } = "Ru";

        // Канонический прод (ANNOUNCE-006, сервер v0.71.0). Дефолт localhost был удобен
        // разработчику и ломал онбординг: свежая установка стучалась в никуда.
        // Старый apic.lcmswantest.tech — переходный алиас, на него не завязываемся.
        public const string DefaultServerAddress = "api.lensacode.ai";

        [JsonPropertyName("serverAddress")]
        public string ServerAddress { get; set; } = DefaultServerAddress;

        [JsonPropertyName("selectedProvider")]
        public string SelectedProvider { get; set; } = string.Empty;

        [JsonPropertyName("selectedModel")]
        public string SelectedModel { get; set; } = string.Empty;

        [JsonPropertyName("selectedSubProvider")]
        public string SelectedSubProvider { get; set; } = string.Empty;

        [JsonPropertyName("selectedFramework")]
        public string SelectedFramework { get; set; } = string.Empty;

        [JsonPropertyName("selectedIde")]
        public string SelectedIde { get; set; } = string.Empty;

        [JsonPropertyName("frameworkVersion")]
        public string FrameworkVersion { get; set; } = string.Empty;

        [JsonPropertyName("projectPath")]
        public string ProjectPath { get; set; } = string.Empty;

        [JsonPropertyName("temperature")]
        public double Temperature { get; set; } = 0.7;

        [JsonPropertyName("conversationMode")]
        public bool ConversationMode { get; set; } = true;

        [JsonPropertyName("useThinking")]
        public bool UseThinking { get; set; }

        // Уровень размышлений minimal|low|medium|high (REQ-007 контракта). Действует при useThinking.
        [JsonPropertyName("reasoningEffort")]
        public string ReasoningEffort { get; set; } = "high";

        // 1С-клиент: известные базы сайдбара («key|name|path», T-000092); Universal игнорирует.
        [JsonPropertyName("knownBases")]
        public System.Collections.Generic.List<string> KnownBases { get; set; } = new();

        // 1С-клиент: BaseKey выбранного конфигуратора — восстановление выбора при старте
        // мгновенно, до фонового скана процессов (смок 07.07); Universal игнорирует.
        [JsonPropertyName("selectedBaseKey")]
        public string SelectedBaseKey { get; set; } = string.Empty;

        // 1С-клиент: режим работы AI planning|auto|review (T-000094, пока визуальный стаб).
        [JsonPropertyName("assistMode")]
        public string AssistMode { get; set; } = "auto";

        [JsonPropertyName("debugMode")]
        public bool DebugMode { get; set; }

        // Диагностический лог трекера имён окон 1С (T-000112). Отдельно от debugMode:
        // включается правкой config.json, НЕ спамит чат [REQUEST]/[RESPONSE] блоками.
        [JsonPropertyName("windowTrackerLog")]
        public bool WindowTrackerLog { get; set; }

        [JsonPropertyName("minimizeToTray")]
        public bool MinimizeToTray { get; set; }

        // 1С-клиент: свёрнут ли сайдбар конфигураторов (T-000099); Universal игнорирует.
        [JsonPropertyName("sidebarCollapsed")]
        public bool SidebarCollapsed { get; set; }

        [JsonPropertyName("alwaysOnTop")]
        public bool AlwaysOnTop { get; set; }

        [JsonPropertyName("userKey")]
        public string UserKey { get; set; } = string.Empty;

        // --- Маркеры изменений кода (1С-клиент, v0.13.0 T-000089; Universal игнорирует) ---

        [JsonPropertyName("changeMarkersEnabled")]
        public bool ChangeMarkersEnabled { get; set; } = true;

        [JsonPropertyName("currentTaskId")]
        public string CurrentTaskId { get; set; } = string.Empty;

        [JsonPropertyName("markerAuthorName")]
        public string MarkerAuthorName { get; set; } = string.Empty;

        [JsonPropertyName("markerTemplate")]
        public string MarkerTemplate { get; set; } = "{task}_{name}_{date}";

        [JsonPropertyName("markerDateFormat")]
        public string MarkerDateFormat { get; set; } = "dd.MM.yyyy";

        // Суффикс закрывающего маркера; пустой = закрывающих нет (заменил булев markerClosingEnabled)
        [JsonPropertyName("markerClosingSuffix")]
        public string MarkerClosingSuffix { get; set; } = "окончание";

        [JsonPropertyName("markerCollapseSeam")]
        public bool MarkerCollapseSeam { get; set; }

        [JsonPropertyName("markerOldAboveNew")]
        public bool MarkerOldAboveNew { get; set; } = true;
    }

    public static class AppConfigService
    {
        /// <summary>Legacy-папка, которую до v0.12.x делили оба продукта.</summary>
        private const string LegacyFolderName = "SwanCodeClient";

        private static string _folderName = LegacyFolderName;

        /// <summary>
        /// Задаёт продуктовую папку настроек (IProductBranding.ConfigFolderName) и один раз
        /// мигрирует *.json из legacy-папки. Вызывать в App.OnStartup до первого Load().
        /// Без вызова работает legacy-путь — поведение до v0.12.x.
        /// </summary>
        public static void Initialize(string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName))
                return;

            _folderName = folderName;
            MigrateFromLegacy();
        }

        /// <summary>Полный путь продуктовой папки настроек (для window.json и т.п.).</summary>
        public static string ConfigDirectory => ConfigDir;

        private static string ConfigDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            _folderName);

        private static string ConfigPath => Path.Combine(ConfigDir, "config.json");

        private static void MigrateFromLegacy()
        {
            try
            {
                if (_folderName == LegacyFolderName)
                    return;

                var legacyDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    LegacyFolderName);
                if (!Directory.Exists(legacyDir))
                    return;

                // Копируем только отсутствующие файлы; legacy-папку не трогаем —
                // второй продукт мигрирует из неё независимо.
                foreach (var src in Directory.GetFiles(legacyDir, "*.json"))
                {
                    var dst = Path.Combine(ConfigDir, Path.GetFileName(src));
                    if (File.Exists(dst))
                        continue;

                    Directory.CreateDirectory(ConfigDir);
                    File.Copy(src, dst);
                }
            }
            catch
            {
                // Миграция best-effort: при сбое продукт стартует с дефолтами
            }
        }

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        public static AppConfig Load()
        {
            try
            {
                if (!File.Exists(ConfigPath))
                    return new AppConfig();

                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
            }
            catch
            {
                return new AppConfig();
            }
        }

        public static void LoadEnvFile()
        {
            try
            {
                var envPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".env");
                if (!File.Exists(envPath))
                    return;

                foreach (var line in File.ReadAllLines(envPath))
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                        continue;

                    var eqIdx = trimmed.IndexOf('=');
                    if (eqIdx <= 0) continue;

                    var key = trimmed[..eqIdx].Trim();
                    var val = trimmed[(eqIdx + 1)..].Trim();
                    Environment.SetEnvironmentVariable(key, val);
                }
            }
            catch
            {
                // Ignore .env read errors
            }
        }

        public static void Save(AppConfig config)
        {
            try
            {
                if (!Directory.Exists(ConfigDir))
                    Directory.CreateDirectory(ConfigDir);

                var json = JsonSerializer.Serialize(config, JsonOptions);
                File.WriteAllText(ConfigPath, json);
            }
            catch
            {
                // Ignore save errors silently
            }
        }
    }
}
