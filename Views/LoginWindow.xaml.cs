using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using LenSa.Core.Branding;
using LenSa.Core.Services.Api;
using LenSa.Core.Services.Theme;

namespace LenSa.Core.Views
{
    public partial class LoginWindow : Window
    {
        // Формат user-ключа задаётся сервером и со временем меняется (Phase 5a был `swan_uk_*` —
        // на v0.11.1 сервер уже отдаёт ключи другого шейпа). Клиент не гадает — принимает любое
        // непустое значение и делегирует проверку серверу (GetMeAsync ниже вернёт UNAUTHORIZED
        // если ключ невалидный или отозван).

        private readonly ApiClient _api;
        private readonly IProductBranding _branding;
        private bool _isBusy;

        /// <summary>
        /// После успешного логина — введённый user-ключ. Сохраняется в config.
        /// </summary>
        public string UserKey { get; private set; } = string.Empty;

        public LoginWindow(IProductBranding branding, ApiClient api)
        {
            _branding = branding ?? throw new ArgumentNullException(nameof(branding));
            _api = api ?? throw new ArgumentNullException(nameof(api));
            InitializeComponent();
            WindowCaptionTheme.Attach(this);
            // Заголовок окна — из брендинга (Core не знает имени конкретного продукта).
            Title = _branding.DisplayName;
            Loaded += (_, _) => KeyBox.Focus();
        }

        private async void LoginButton_Click(object sender, RoutedEventArgs e)
        {
            await DoLoginAsync();
        }

        private void KeyBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                _ = DoLoginAsync();
        }

        private async Task DoLoginAsync()
        {
            if (_isBusy) return;

            var key = KeyBox.Password.Trim();

            if (string.IsNullOrEmpty(key))
            {
                ShowError(Localize("str_InvalidKeyFormat"));
                return;
            }

            _isBusy = true;
            LoginButton.IsEnabled = false;
            ErrorText.Visibility = Visibility.Collapsed;
            StatusText.Text = Localize("str_LoggingIn");
            StatusText.Visibility = Visibility.Visible;

            // Временно ставим ключ и проверяем через GetMeAsync. При неудаче откатываем.
            var previousKey = _api.UserKey;
            _api.UserKey = key;

            try
            {
                var me = await _api.GetMeAsync();
                if (me.ServiceInfo.Success)
                {
                    UserKey = key;
                    DialogResult = true;
                    Close();
                }
                else
                {
                    _api.UserKey = previousKey;
                    ShowError(MapErrorCode(me.ServiceInfo.ErrorCode));
                }
            }
            catch (ApiException ex)
            {
                _api.UserKey = previousKey;
                ShowError(MapErrorCode(ex.ErrorCode, ex.Message));
            }
            catch (Exception ex)
            {
                _api.UserKey = previousKey;
                ShowError(ex.Message);
            }
            finally
            {
                _isBusy = false;
                LoginButton.IsEnabled = true;
                StatusText.Visibility = Visibility.Collapsed;
            }
        }

        private string MapErrorCode(string? code, string? fallback = null)
        {
            var localized = code switch
            {
                "UNAUTHORIZED" or "USER_REQUIRED" => Localize("str_InvalidUserKey"),
                "USER_DELETED" => Localize("str_UserDeleted"),
                "PRODUCT_REQUIRED" => Localize("str_NoProductKey"),
                "CONNECTION_ERROR" => Localize("str_ErrorConnection"),
                _ => fallback ?? code ?? Localize("str_InvalidUserKey")
            };
            // Диагностика: показываем сырой errorCode и (если есть) серверный message —
            // чтобы различать UNAUTHORIZED (ключ не найден) / USER_REQUIRED (роль не подошла) /
            // KEY_VALIDATION_UNAVAILABLE / прочие случаи, пока текст одинаковый.
            if (!string.IsNullOrEmpty(code))
            {
                var suffix = !string.IsNullOrEmpty(fallback) && fallback != code
                    ? $" [{code}: {fallback}]"
                    : $" [{code}]";
                return localized + suffix;
            }
            return localized;
        }

        private string Localize(string key)
        {
            return (string)FindResource(key) ?? key;
        }

        private void ShowError(string message)
        {
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
