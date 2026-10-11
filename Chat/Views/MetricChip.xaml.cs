using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        public static readonly DependencyProperty ShowFooterProperty = DependencyProperty.Register(
            nameof(ShowFooter), typeof(bool), typeof(MetricChip), new PropertyMetadata(true, FooterChanged));
        private static WeakReference<MetricChip>? _openChip;

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
        public bool IsCardOpen => CardPopup.IsOpen;

        public MetricChip()
        {
            InitializeComponent();
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
        private void Message_PropertyChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

        private void Refresh()
        {
            if (Card is null) return;
            Card.Message = Message;
            Card.Refresh();
            var data = Message is null ? null : MetricCardBuilder.Build(Message, CultureInfo.CurrentCulture);
            FooterModel.Text = data?.ModelName ?? string.Empty;
            FooterTime.Text = data?.TimeText ?? string.Empty;
            FooterModel.Visibility = ShowFooter && !string.IsNullOrEmpty(FooterModel.Text)
                ? Visibility.Visible : Visibility.Collapsed;
            FooterTime.Visibility = ShowFooter && !string.IsNullOrEmpty(FooterTime.Text)
                ? Visibility.Visible : Visibility.Collapsed;
            InfoButton.ToolTip = BuildQuickTip(data);
        }

        private ToolTip BuildQuickTip(MetricCardData? data)
        {
            var body = new StackPanel();
            if (data?.CoinsText is string coins)
                AddQuickRow(body, "str_MetricCoins", coins, "str_MetricCoinsUnit");
            if (data?.DateText is string date)
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
            Refresh();
            var host = HostVisual();
            var maxWidth = host?.ActualWidth > 16 ? Math.Min(330, host.ActualWidth - 16) : 330;
            Card.Width = double.NaN;
            Card.MaxWidth = maxWidth;
            CardScroll.Width = maxWidth;
            CardScroll.MaxWidth = maxWidth;
            CardScroll.MaxHeight = host?.ActualHeight > 16 ? host.ActualHeight - 16 : 540;
            _openChip = new WeakReference<MetricChip>(this);
            CardPopup.IsOpen = true;
            CardScroll.Focus();
        }

        public void CloseCard() { if (CardPopup is not null) CardPopup.IsOpen = false; }
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

        private void InfoButton_Click(object sender, RoutedEventArgs e) => ToggleCard();
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
