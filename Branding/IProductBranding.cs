namespace LenSa.Core.Branding
{
    /// <summary>
    /// Описывает product-specific брендинг, который ядро (Core) не знает в compile-time:
    /// человекочитаемое имя продукта, product-key для отправки на сервер (X-Product-Key),
    /// акцентный цвет UI. Реализации — lensa_code_client (1С) и lensa_code_universal.
    /// </summary>
    public interface IProductBranding
    {
        /// <summary>
        /// Машинный ключ продукта: секрет вида `lens_pk_*`, выданный сервером на продукт
        /// (ранее выпущенные `swan_pk_*` принимаются бессрочно). Вшивается при публикации.
        /// Уходит на сервер заголовком X-Product-Key.
        /// </summary>
        string ProductKey { get; }

        /// <summary>
        /// Человекочитаемое имя продукта (например, "LenSa Dev 1C" или "LenSa Universal").
        /// Подставляется в заголовки окон, тостеры, диалоги ядра.
        /// </summary>
        string DisplayName { get; }

        /// <summary>
        /// Акцентный цвет в формате HTML hex ("#RRGGBB"). В v0.11.0 одинаков для всех продуктов ("#0066CC").
        /// </summary>
        string AccentColorHex { get; }

        /// <summary>
        /// Имя папки продукта под %LocalAppData% для config.json / window.json и т.п.
        /// (например, "LenSaDev1C" или "LenSaUniversal"). До v0.12.x оба продукта
        /// делили одну папку "SwanCodeClient" — теперь каждый хранит настройки у себя;
        /// AppConfigService.Initialize мигрирует старые файлы при первом старте.
        /// </summary>
        string ConfigFolderName { get; }

        /// <summary>Optional product-specific page explaining how to obtain a user key.</summary>
        string? GetKeyUrl => null;
    }
}
