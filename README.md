# LenSa.Core

Общая библиотека для клиентов LenSa Dev 1С ([lensa_code_client](https://github.com/SgonnovDmGit/lensa_code_client)) и LenSa Universal ([lensa_code_universal](https://github.com/SgonnovDmGit/lensa_code_universal)).

## Содержимое

- Helpers — MVVM-инфра (ViewModelBase, RelayCommand, Converters)
- Services/Api — ApiClient + ApiModels (серверный HTTP-контракт)
- Services/Auth — Phase 5a auth (OTP + UserKey)
- Services/AppConfig — настройки приложения (window.json и т.п.)
- Services/Theme — ThemeManager; WindowCaptionTheme.Attach красит системную шапку кистями TitleBarBackground/TitleBarForeground и снимает подписку на Closed. LoginWindow и ErrorExplainDialog подключают его в конструкторе для любого потребителя; отсутствующие/неоднотонные кисти и неподдержанные DWM-атрибуты оставляют системную шапку.
- Themes — Light + Dark (1C тема — только в lensa_code_client)
- Views — LoginWindow, Dialogs
- Chat — ChatViewModelBase + ChatView (общий чат-каркас)
- Branding — IProductBranding (display name, акцентный цвет per product)
- Localization — Core.ru.xaml / Core.en.xaml (общие UI-ключи)

## Использование

Подключается в продуктовые клиенты как git submodule в папку `core/`. Не публикуется отдельно как NuGet/Release.

### Темы и продуктовые слои

`ThemeManager.Initialize(resources, themeIndex, coreStringsIndex, productStringsIndex, assemblyName)`
регистрирует обычные темы Core без продуктового слоя. Перегрузка с
`IReadOnlyDictionary<AppTheme, Uri>? productOverlays` собирает тему из базового словаря
и слоя продукта; слой перекрывает базу. Инициализация применяет текущую тему сразу,
включая старт со светлой темой без последующего `SetTheme`.

Новые ключи поверхностей `ChatFeedBackground`, `TaskBoard*`, `Composer*`, `ChatHeaderBackground`,
`ChatPicker*` и `ContextOkColor` по умолчанию повторяют прежние цвета Core.
`ComposerFocusThickness` по умолчанию равен 1; слой рамки совпадает с обычной границей.
Продукт переопределяет эти ресурсы в своих словарях, не меняя палитру другого клиента.
`IProductBranding.GetKeyUrl` по умолчанию `null`: ссылка в окне входа скрыта.

### Лента, цитата и подтверждения

`ChatView` обрабатывает изменение размера только своего внешнего `ScrollViewer`.
При росте/сжатии композера лента, которая была внизу, остаётся внизу; якорь догрузки
истории сохраняет прежний путь. Границы полного хода вычисляет
`ChatTurnLayout.TurnRange`; тот же метод используется для полного текста и
проверки принадлежности выделения. Цитируется видимое выделение текущего хода
либо весь markdown хода. Выделение забывается после ухода фокуса, кроме перехода
к кнопке цитаты того же хода; уход с этой кнопки также завершает выделение.
После цитаты `ChatInputBar.FocusInputAtEnd` ставит каретку в конец и прокручивает поле.

`ThemedMessageDialog.Show` показывает модальное подтверждение с явными результатами
кнопок; Esc и крестик возвращают `cancelResult`. Для автоматических тестов есть
`Override`, который нужно восстанавливать в `finally`. Удаление задачи после ответа
повторно проверяет её наличие в текущей доске. Общий интерфейс обоих клиентов
получает новую форму кнопки «i», цитирование/прокрутку и тематическое удаление задачи.

### Обратная связь

`ApiClient.PostFeedbackAsync(text, kind, rating, cancellationToken)` отправляет
`POST /v1/feedback` с обычными user/product заголовками. Невыбранный `rating`
отсутствует в JSON. Успех требует HTTP 200 и `serviceInfo.success=true`;
отсутствующий `id` успеху не мешает. Метаданные ошибок `retryAfterSeconds`,
`minLength`, `maxLength` доступны в `ApiException` и необязательны для старых вызовов.
Токен отменяет HTTP-ожидание; сам по себе он не подтверждает, что сервер не успел
доставить обращение. Автоматического повтора отправки нет.

## Версионирование

Версия в .csproj обновляется при значимых изменениях API. Конкретный SHA core'а фиксируется submodule-указателем в продуктовом репо.
