using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LenSa.Core.Chat.Views
{
    /// <summary>
    /// Композер: живёт под лентой чата, внутри колонки чата (T-000121). DataContext —
    /// продуктовый MainViewModel с ActiveChat и ProjectSettings (биндинги duck-typed по пути).
    /// </summary>
    public partial class ChatInputBar : UserControl
    {
        /// <summary>
        /// Показывать чип режима (План/Ревью/Авто). Только 1С-клиент: у Universal нет
        /// AssistMode в ProjectSettings, и чип привязался бы к несуществующему свойству.
        /// </summary>
        public static readonly DependencyProperty ShowModeChipProperty =
            DependencyProperty.Register(nameof(ShowModeChip), typeof(bool), typeof(ChatInputBar),
                new PropertyMetadata(false));

        public bool ShowModeChip
        {
            get => (bool)GetValue(ShowModeChipProperty);
            set => SetValue(ShowModeChipProperty, value);
        }

        public ChatInputBar()
        {
            InitializeComponent();
        }

        public void FocusInputAtEnd()
        {
            InputTextBox.Focus();
            InputTextBox.CaretIndex = InputTextBox.Text.Length;
            InputTextBox.ScrollToEnd();
        }

        // Enter → отправка; Shift+Enter → перенос строки (AcceptsReturn=true).
        // По memory feedback_wpf_textbox_enter — обязательно PreviewKeyDown, не KeyDown.
        private void InputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift) return;

            if (SendButton.Command?.CanExecute(null) == true)
            {
                SendButton.Command.Execute(null);
                e.Handled = true;
            }
        }

        // Выбор режима закрывает меню — иначе оно висит поверх ленты до клика мимо.
        private void ModeItem_Click(object sender, RoutedEventArgs e) => ModeChip.IsChecked = false;

        // То же для модели: клик по элементу списка происходит ВНУТРИ Popup, поэтому
        // StaysOpen=False не считает его «кликом мимо» и меню остаётся висеть.
        private void ModelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count > 0) ModelChip.IsChecked = false;
        }
    }
}
