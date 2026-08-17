# LenSa.Core

Общая библиотека для клиентов Swan Code 1C ([swan_code_client](https://github.com/SgonnovDmGit/swan_code_client)) и Swan Code Universal ([swan_code_universal](https://github.com/SgonnovDmGit/swan_code_universal)).

## Содержимое

- Helpers — MVVM-инфра (ViewModelBase, RelayCommand, Converters)
- Services/Api — ApiClient + ApiModels (серверный HTTP-контракт)
- Services/Auth — Phase 5a auth (OTP + UserKey)
- Services/AppConfig — настройки приложения (window.json и т.п.)
- Services/Theme — ThemeManager
- Themes — Light + Dark (1C тема — только в swan_code_client)
- Views — LoginWindow, Dialogs
- Chat — ChatViewModelBase + ChatView (общий чат-каркас)
- Branding — IProductBranding (display name, акцентный цвет per product)
- Localization — Core.ru.xaml / Core.en.xaml (общие UI-ключи)

## Использование

Подключается в продуктовые клиенты как git submodule в папку `core/`. Не публикуется отдельно как NuGet/Release.

## Версионирование

Версия в .csproj обновляется при значимых изменениях API. Конкретный SHA core'а фиксируется submodule-указателем в продуктовом репо.
