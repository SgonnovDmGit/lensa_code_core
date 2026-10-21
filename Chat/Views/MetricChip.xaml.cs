using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Media;
using LenSa.Core.Chat.Models;
using LenSa.Core.Helpers;

namespace LenSa.Core.Chat.Views
{
    /// <summary>Metrics footer, quick hover and keyboard-accessible card. Data selection is pure.</summary>
    public partial class MetricChip : UserControl
    {
        public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
            nameof(Message), typeof(ChatMessage), typeof(MetricChip), new PropertyMetadata(null, MessageChanged));
        public static readonly DependencyProperty ShowInfoProperty = DependencyProperty.Register(
            nameof(ShowInfo), typeof(bool), typeof(MetricChip), new PropertyMetadata(true, InfoChanged));
        public static readonly DependencyProperty ShowFooterProperty = DependencyProperty.Register(
            nameof(ShowFooter), typeof(bool), typeof(MetricChip), new PropertyMetadata(true, FooterChanged));
        private static readonly DependencyProperty CardOpenStateProperty = DependencyProperty.Register(
            "CardOpenState", typeof(bool), typeof(MetricChip), new PropertyMetadata(false, CardOpenStateChanged));
        private static WeakReference<MetricChip>? _openChip;
        private MetricCardView? _card;

        public ChatMessage? Message
        {
            get => (ChatMessage?)GetValue(MessageProperty);
            set => SetValue(MessageProperty, value);
        }
        public bool ShowFooter
        {
            get => (bool)GetValue(ShowFooterProperty);
            set => SetValue(ShowFooterProperty, value);
        }
        public bool ShowInfo
        {
            get => (bool)GetValue(ShowInfoProperty);
            set => SetValue(ShowInfoProperty, value);
        }
        public bool IsCardOpen => CardPopup.IsOpen;
        private MetricCardView? Card => _card;
        private bool _awaitingAnchorMouseDown;
        private bool _suppressClickAfterPopupMouseClose;
        private bool _closingCardExplicitly;

        public MetricChip()
        {
            InitializeComponent();
            SetBinding(CardOpenStateProperty, new Binding(nameof(Popup.IsOpen)) { Source = CardPopup, Mode = BindingMode.OneWay });
            CardPopup.CustomPopupPlacementCallback = PlaceCard;
            Loaded += Chip_Loaded;
        }

        private static void MessageChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            var chip = (MetricChip)sender;
            if (e.OldValue is ChatMessage old) old.PropertyChanged -= chip.Message_PropertyChanged;
            if (e.NewValue is ChatMessage current) current.PropertyChanged += chip.Message_PropertyChanged;
            chip.CloseCard();
            chip.Refresh();
        }
        private static void FooterChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
            ((MetricChip)sender).Refresh();
        private static void InfoChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
            ((MetricChip)sender).Refresh();
        private void Message_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is not (nameof(ChatMessage.ModelName) or nameof(ChatMessage.ReasoningEffort)
                or nameof(ChatMessage.Chain) or nameof(ChatMessage.CallMetrics) or nameof(ChatMessage.CheckpointTrigger)
                or nameof(ChatMessage.CheckpointTokensBefore) or nameof(ChatMessage.CheckpointWindowTokensAfterEstimate)
                or nameof(ChatMessage.CheckpointWindowTokensAfterActual)))
                return;
            Refresh(rebuildCard: IsCardOpen);
        }

        private void Refresh(bool rebuildCard = false)
        {
            if (rebuildCard && Card is not null)
            {
                if (!ReferenceEquals(Card.Message, Message)) Card.Message = Message;
                else Card.Refresh();
            }
            var stats = Message?.Chain ?? Message?.CallMetrics;
            var model = Message?.ModelName;
            var time = stats?.CreatedAt?.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);
            FooterModel.Text = model ?? string.Empty;
            FooterTime.Text = time ?? string.Empty;
            FooterModel.Visibility = ShowFooter && !string.IsNullOrEmpty(FooterModel.Text)
                ? Visibility.Visible : Visibility.Collapsed;
            FooterTime.Visibility = ShowFooter && !string.IsNullOrEmpty(FooterTime.Text)
                ? Visibility.Visible : Visibility.Collapsed;
            InfoButton.Visibility = ShowInfo ? Visibility.Visible : Visibility.Collapsed;
            InfoButton.ToolTip = BuildQuickTip(
                stats?.CostCoins?.ToString("F2", CultureInfo.CurrentCulture),
                stats?.CreatedAt?.ToLocalTime().ToString("dd.MM.yyyy · HH:mm", CultureInfo.CurrentCulture));
        }

        private ToolTip BuildQuickTip(string? coins, string? date)
        {
            var body = new StackPanel();
            if (coins is not null)
                AddQuickRow(body, "str_MetricCoins", coins, "str_MetricCoinsUnit");
            if (date is not null)
                AddQuickRow(body, "str_MetricDate", date, null);
            var hint = MetricCardView.Label("str_MetricClickHint", 10);
            hint.Margin = new Thickness(0, 5, 0, 0);
            var hintBorder = new Border { BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 5, 0, 0), Margin = new Thickness(0, 5, 0, 0), Child = hint };
            hintBorder.SetResourceReference(Border.BorderBrushProperty, "BorderColor");
            body.Children.Add(hintBorder);
            var tip = new ToolTip { Content = body, Padding = new Thickness(10, 8, 10, 8),
                Placement = PlacementMode.Top };
            tip.SetResourceReference(Control.BackgroundProperty, "PopupBackground");
            tip.SetResourceReference(Control.BorderBrushProperty, "PopupBorder");
            tip.SetResourceReference(Control.ForegroundProperty, "PrimaryForeground");
            return tip;
        }

        private static void AddQuickRow(Panel body, string key, string value, string? unit)
        {
            var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(MetricCardView.Label(key, 11));
            var text = MetricCardView.Value(value, unit, 11);
            text.Margin = new Thickness(15, 0, 0, 0);
            text.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            body.Children.Add(row);
        }

        public void ToggleCard()
        {
            if (IsCardOpen) { CloseCard(); return; }
            if (Message is null) return;
            if (_openChip?.TryGetTarget(out var previous) == true && previous != this)
                previous.CloseCard();
            var card = EnsureCard();
            Refresh(rebuildCard: true);
            var host = HostVisual();
            var maxWidth = host?.ActualWidth > 16 ? Math.Min(330, host.ActualWidth - 16) : 330;
            card.Width = double.NaN;
            card.MaxWidth = maxWidth;
            CardScroll.Width = maxWidth;
            CardScroll.MaxWidth = maxWidth;
            CardScroll.MaxHeight = host?.ActualHeight > 16 ? host.ActualHeight - 16 : 540;
            _openChip = new WeakReference<MetricChip>(this);
            CardPopup.IsOpen = true;
            CardScroll.Focus();
        }

        public void CloseCard()
        {
            if (_openChip?.TryGetTarget(out var chip) == true && chip == this) _openChip = null;
            if (CardPopup is null) return;
            _closingCardExplicitly = true;
            try { CardPopup.IsOpen = false; }
            finally { _closingCardExplicitly = false; }
        }

        private MetricCardView EnsureCard()
        {
            if (Card is not null) return Card;
            _card = new MetricCardView();
            CardScroll.Content = _card;
            return _card;
        }

        private FrameworkElement? HostVisual() => Window.GetWindow(this) ??
            PresentationSource.FromVisual(InfoButton)?.RootVisual as FrameworkElement;

        private CustomPopupPlacement[] PlaceCard(Size popupSize, Size targetSize, Point offset)
        {
            var host = HostVisual();
            if (host is null)
                return new[] { new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width,
                    -popupSize.Height - 5), PopupPrimaryAxis.Vertical) };
            var targetOrigin = InfoButton.TransformToAncestor(host).Transform(new Point());
            var target = new Rect(targetOrigin, targetSize);
            var bounds = new Rect(8, 8, Math.Max(0, host.ActualWidth - 16), Math.Max(0, host.ActualHeight - 16));
            var point = MetricPopupPlacement.Calculate(target, popupSize, bounds);
            return new[] { new CustomPopupPlacement(point - (Vector)targetOrigin, PopupPrimaryAxis.None) };
        }

        private void InfoButton_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_awaitingAnchorMouseDown)
            {
                _awaitingAnchorMouseDown = false;
                _suppressClickAfterPopupMouseClose = true;
                return;
            }
            // A prior down without its matching up was interrupted; do not let it consume this gesture.
            _suppressClickAfterPopupMouseClose = false;
            if (IsCardOpen)
            {
                _suppressClickAfterPopupMouseClose = true;
                CloseCard();
                _awaitingAnchorMouseDown = false;
            }
        }
        private void InfoButton_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            _awaitingAnchorMouseDown = false;
            if (!_suppressClickAfterPopupMouseClose) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                _suppressClickAfterPopupMouseClose = false));
        }
        private void InfoButton_Click(object sender, RoutedEventArgs e)
        {
            if (_suppressClickAfterPopupMouseClose)
            {
                _suppressClickAfterPopupMouseClose = false;
                e.Handled = true;
                return;
            }
            ToggleCard();
        }
        private void InfoButton_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key is Key.Enter or Key.Space) { ToggleCard(); e.Handled = true; }
            else if (e.Key == Key.Escape) { CloseCard(); e.Handled = true; }
        }
        private void CardScroll_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { CloseCard(); InfoButton.Focus(); e.Handled = true; }
        }
        private void CardPopup_Closed(object sender, EventArgs e)
        {
            if (_openChip?.TryGetTarget(out var chip) == true && chip == this) _openChip = null;
        }
        private static void CardOpenStateChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            var chip = (MetricChip)sender;
            // IsOpen changes during native outside-dismiss, before the anchor gets the down.
            // Closed is deferred until HWND destruction and can arrive after Button.Click.
            if (!(bool)e.NewValue && !chip._closingCardExplicitly &&
                Mouse.LeftButton == MouseButtonState.Pressed &&
                new Rect(new Point(), chip.InfoButton.RenderSize).Contains(Mouse.GetPosition(chip.InfoButton)))
            {
                chip._awaitingAnchorMouseDown = true;
                chip.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    if (Mouse.LeftButton == MouseButtonState.Released)
                        chip._awaitingAnchorMouseDown = false;
                }));
            }
        }
        private void Chip_Loaded(object sender, RoutedEventArgs e)
        {
            if (Message is { } message)
            {
                message.PropertyChanged -= Message_PropertyChanged;
                message.PropertyChanged += Message_PropertyChanged;
            }
            Refresh();
        }
        private void Chip_Unloaded(object sender, RoutedEventArgs e)
        {
            CloseCard();
            if (Message is { } message) message.PropertyChanged -= Message_PropertyChanged;
        }
    }
}
