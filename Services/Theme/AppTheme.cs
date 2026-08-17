namespace LenSa.Core.Services.Theme
{
    /// <summary>
    /// Список доступных тем оформления приложения.
    /// Light/Dark живут в LenSa.Core, OneC — в продуктовой сборке (swan_code_client).
    /// </summary>
    public enum AppTheme
    {
        Light,
        Dark,
        OneC
    }

    /// <summary>
    /// Языки локализации UI.
    /// </summary>
    public enum AppLanguage
    {
        Ru,
        En
    }
}
