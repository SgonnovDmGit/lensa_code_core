using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using LenSa.Core.Chat.Models;
using LenSa.Core.Chat.ViewModels;

namespace LenSa.Core.Chat.Views
{
    public partial class ChatView : UserControl
    {
        private FrameworkElement? _prependAnchor;
        private double _prependAnchorY;
        private double _prependExtentHeight;
        private bool _prependRestoreQueued;

        public ChatView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is ChatViewModelBase oldVm)
                oldVm.Messages.CollectionChanged -= OnMessagesChanged;
            if (e.NewValue is ChatViewModelBase newVm)
                newVm.Messages.CollectionChanged += OnMessagesChanged;
        }

        private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action != NotifyCollectionChangedAction.Add) return;

            var addedCount = e.NewItems?.Count ?? 0;
            var hadMessagesBeforeAdd = sender is System.Collections.ICollection messages &&
                                       messages.Count > addedCount;
            if (e.NewStartingIndex == 0 && hadMessagesBeforeAdd)
            {
                PreserveViewportAfterPrepend();
                return;
            }

            // Append scrolls to the latest message; prepend keeps the visible anchor in place.
            Dispatcher.BeginInvoke(new System.Action(() => ThreadScroll.ScrollToEnd()),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private void PreserveViewportAfterPrepend()
        {
            if (_prependRestoreQueued) return;

            _prependRestoreQueued = true;
            _prependExtentHeight = ThreadScroll.ExtentHeight;
            _prependAnchor = FindFirstVisibleMessage(out _prependAnchorY);
            Dispatcher.BeginInvoke(new System.Action(RestorePrependViewport),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private FrameworkElement? FindFirstVisibleMessage(out double y)
        {
            y = 0;
            for (var index = 0; index < MessageItems.Items.Count; index++)
            {
                if (MessageItems.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement item ||
                    !item.IsVisible)
                    continue;

                try
                {
                    var position = item.TransformToAncestor(ThreadScroll).Transform(new Point(0, 0));
                    if (position.Y + item.ActualHeight <= 0 || position.Y >= ThreadScroll.ViewportHeight)
                        continue;

                    y = position.Y;
                    return item;
                }
                catch (InvalidOperationException)
                {
                    // Layout may not have attached this container yet; extent delta is the fallback.
                }
            }

            return null;
        }

        private void RestorePrependViewport()
        {
            ThreadScroll.UpdateLayout();
            if (_prependAnchor is { IsLoaded: true } anchor)
            {
                try
                {
                    var newY = anchor.TransformToAncestor(ThreadScroll).Transform(new Point(0, 0)).Y;
                    ThreadScroll.ScrollToVerticalOffset(ThreadScroll.VerticalOffset + newY - _prependAnchorY);
                }
                catch (InvalidOperationException)
                {
                    RestoreByExtentDelta();
                }
            }
            else
            {
                RestoreByExtentDelta();
            }

            _prependAnchor = null;
            _prependRestoreQueued = false;
        }

        private void RestoreByExtentDelta() =>
            ThreadScroll.ScrollToVerticalOffset(
                ThreadScroll.VerticalOffset + ThreadScroll.ExtentHeight - _prependExtentHeight);
        // --- Кнопки-иконки под сообщением ассистента (T-000074) --------------

        private void CopyMessage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not ChatMessage msg) return;
            try { Clipboard.SetText(msg.Content ?? string.Empty); }
            catch { return; /* клипборд занят другим процессом */ }

            // ✓-фидбек: иконка меняется на галочку SuccessColor на ~1.2 с
            var original = btn.Content;
            var check = new Path
            {
                Width = 13,
                Height = 13,
                Stretch = Stretch.Uniform,
                Data = Geometry.Parse("M9 16.2 4.8 12l-1.4 1.4L9 19 21 7l-1.4-1.4z")
            };
            check.SetResourceReference(Shape.FillProperty, "SuccessColor");
            btn.Content = check;
            btn.IsEnabled = false;

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = System.TimeSpan.FromMilliseconds(1200)
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                btn.Content = original;
                btn.IsEnabled = true;
            };
            timer.Start();
        }

        private void QuoteMessage_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ChatMessage msg) return;
            if (DataContext is not ChatViewModelBase vm) return;

            var lines = (msg.Content ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            var quoted = string.Join("\n", System.Linq.Enumerable.Select(lines, l => "> " + l)) + "\n\n";
            vm.InputText = quoted + vm.InputText;
        }

        // Спиннер running-карточки тула: вращение стартуем кодом на локальном
        // (незамороженном) RotateTransform — Storyboard из Style-триггера на
        // шаблонном трансформе кидает InvalidOperationException (frozen instance).
        private void Spinner_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Path path) return;
            var rotate = new RotateTransform();
            path.RenderTransform = rotate;
            rotate.BeginAnimation(RotateTransform.AngleProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, 360, System.TimeSpan.FromSeconds(0.8))
                {
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
                });
        }

        // RichTextBox сообщений глотает колесо мыши даже без своих скроллбаров —
        // пробрасываем событие родительскому ScrollViewer треда.
        private void MessageBody_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            ForwardMouseWheelToThread(sender, e);
        }

        private void Summary_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled || sender is not ScrollViewer summary) return;

            var canScrollInside = e.Delta > 0
                ? summary.VerticalOffset > 0
                : summary.VerticalOffset < summary.ScrollableHeight;
            if (!canScrollInside)
                ForwardMouseWheelToThread(sender, e);
        }

        private void ForwardMouseWheelToThread(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled) return;
            e.Handled = true;
            ThreadScroll.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = sender
            });
        }
    }
}
