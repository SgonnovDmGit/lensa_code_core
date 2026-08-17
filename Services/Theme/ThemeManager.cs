using System;
using System.Windows;

namespace LenSa.Core.Services.Theme
{
    /// <summary>
    /// Менеджер тем и языка. 3-индексное API: [themeIndex] тема + [coreStringsIndex] core-строки + [productStringsIndex] продуктовые строки.
    /// Тема: Light/Dark берутся из LenSa.Core, OneC — из продуктовой сборки (относительный URI резолвится её StartupUri).
    /// Локализация: при смене языка одновременно подменяются core-строки и продуктовые строки.
    /// </summary>
    public static class ThemeManager
    {
        private static int _themeIndex;
        private static int _coreStringsIndex;
        private static int _productStringsIndex;
        private static ResourceDictionary? _appResources;
        private static string? _productAssemblyName;

        public static AppTheme CurrentTheme { get; private set; } = AppTheme.Light;
        public static AppLanguage CurrentLanguage { get; private set; } = AppLanguage.Ru;

        public static event Action? ThemeChanged;
        public static event Action? LanguageChanged;

        /// <summary>
        /// Инициализация менеджера. Должна быть вызвана в App.OnStartup до первого SetTheme/SetLanguage.
        /// </summary>
        /// <param name="appResources">Application.Current.Resources продуктовой сборки.</param>
        /// <param name="themeIndex">Индекс MergedDictionary с темой (обычно 0).</param>
        /// <param name="coreStringsIndex">Индекс MergedDictionary с core-локализацией (обычно 1).</param>
        /// <param name="productStringsIndex">Индекс MergedDictionary с продуктовой локализацией (обычно 2).</param>
        /// <param name="productAssemblyName">Имя продуктовой сборки для pack URI (например, "swan_code_client").</param>
        public static void Initialize(
            ResourceDictionary appResources,
            int themeIndex,
            int coreStringsIndex,
            int productStringsIndex,
            string productAssemblyName)
        {
            if (appResources is null) throw new ArgumentNullException(nameof(appResources));
            if (string.IsNullOrEmpty(productAssemblyName)) throw new ArgumentException("Product assembly name required", nameof(productAssemblyName));

            var required = Math.Max(themeIndex, Math.Max(coreStringsIndex, productStringsIndex)) + 1;
            if (appResources.MergedDictionaries.Count < required)
            {
                throw new InvalidOperationException(
                    $"App resources require at least {required} MergedDictionaries " +
                    $"(theme={themeIndex}, coreStrings={coreStringsIndex}, productStrings={productStringsIndex}).");
            }

            _appResources = appResources;
            _themeIndex = themeIndex;
            _coreStringsIndex = coreStringsIndex;
            _productStringsIndex = productStringsIndex;
            _productAssemblyName = productAssemblyName;
        }

        /// <summary>
        /// Сменить активную тему. Light/Dark из Core, OneC — из продуктовой сборки.
        /// </summary>
        public static void SetTheme(AppTheme theme)
        {
            EnsureInitialized();
            if (theme == CurrentTheme)
                return;

            var uri = GetThemeUri(theme);
            _appResources!.MergedDictionaries[_themeIndex] = new ResourceDictionary { Source = uri };
            CurrentTheme = theme;
            ThemeChanged?.Invoke();
        }

        /// <summary>
        /// Сменить язык. Одновременно подменяются core-строки и продуктовые строки.
        /// </summary>
        public static void SetLanguage(AppLanguage language)
        {
            EnsureInitialized();
            if (language == CurrentLanguage)
                return;

            var langCode = language switch
            {
                AppLanguage.Ru => "ru",
                AppLanguage.En => "en",
                _ => "ru"
            };

            var coreUri = new Uri(
                $"pack://application:,,,/LenSa.Core;component/Localization/Core.{langCode}.xaml",
                UriKind.Absolute);
            var productUri = new Uri(
                $"pack://application:,,,/{_productAssemblyName};component/Localization/Strings.{langCode}.xaml",
                UriKind.Absolute);

            _appResources!.MergedDictionaries[_coreStringsIndex] = new ResourceDictionary { Source = coreUri };
            _appResources!.MergedDictionaries[_productStringsIndex] = new ResourceDictionary { Source = productUri };
            CurrentLanguage = language;
            LanguageChanged?.Invoke();
        }

        private static Uri GetThemeUri(AppTheme theme)
        {
            return theme switch
            {
                AppTheme.Light => new Uri(
                    "pack://application:,,,/LenSa.Core;component/Themes/ThemeLight.xaml",
                    UriKind.Absolute),
                AppTheme.Dark => new Uri(
                    "pack://application:,,,/LenSa.Core;component/Themes/ThemeDark.xaml",
                    UriKind.Absolute),
                AppTheme.OneC => new Uri(
                    $"pack://application:,,,/{_productAssemblyName};component/Themes/Theme1C.xaml",
                    UriKind.Absolute),
                _ => new Uri(
                    "pack://application:,,,/LenSa.Core;component/Themes/ThemeLight.xaml",
                    UriKind.Absolute)
            };
        }

        private static void EnsureInitialized()
        {
            if (_appResources is null || _productAssemblyName is null)
                throw new InvalidOperationException("ThemeManager.Initialize must be called before SetTheme/SetLanguage.");
        }
    }
}
