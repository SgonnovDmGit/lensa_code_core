using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
        private WeakReference<RichTextBox>? _selectedMessageBody;

        public ChatView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            ForgetSelection();
            if (e.OldValue is ChatViewModelBase oldVm)
                oldVm.Messages.CollectionChanged -= OnMessagesChanged;
            if (e.NewValue is ChatViewModelBase newVm)
                newVm.Messages.CollectionChanged += OnMessagesChanged;
        }

        private void ThreadScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (!ReferenceEquals(e.OriginalSource, ThreadScroll)) return;
            var wasAtBottom = e.VerticalOffset - e.VerticalChange >=
                (e.ExtentHeight - e.ExtentHeightChange) -
                (e.ViewportHeight - e.ViewportHeightChange) - 40;
            if (e.ViewportHeightChange != 0 && wasAtBottom)
                ThreadScroll.ScrollToEnd();
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

                    var textAnchor = FindFirstVisibleText(item);
                    if (textAnchor != null)
                    {
                        try
                        {
                            y = FirstTextPositionInThread(textAnchor);
                            return textAnchor;
                        }
                        catch (InvalidOperationException)
                        {
                            // Use the stable row as a fallback while a text surface is retemplating.
                        }
                    }

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
                    var newY = anchor is RichTextBox text
                        ? FirstTextPositionInThread(text)
                        : anchor.TransformToAncestor(ThreadScroll).Transform(new Point(0, 0)).Y;
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

        private static RichTextBox? FindFirstVisibleText(DependencyObject root)
        {
            if (root is RichTextBox text && text.IsVisible &&
                !string.IsNullOrWhiteSpace(new TextRange(text.Document.ContentStart, text.Document.ContentEnd).Text))
                return text;

            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                var found = FindFirstVisibleText(VisualTreeHelper.GetChild(root, index));
                if (found != null) return found;
            }

            return null;
        }

        private double FirstTextPositionInThread(RichTextBox text)
        {
            var firstCharacter = text.Document.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            return text.TransformToAncestor(ThreadScroll)
                .Transform(new Point(firstCharacter.X, firstCharacter.Y)).Y;
        }
        // --- Кнопки-иконки под сообщением ассистента (T-000074) --------------

        private void CopyMessage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.DataContext is not ChatMessage msg) return;
            if (!btn.IsEnabled) return;
            if (DataContext is not ChatViewModelBase vm) return;
            var text = ChatTurnLayout.CollectText(vm.Messages, msg);
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); }
            catch { return; /* клипборд занят другим процессом */ }

            // ✓-фидбек: иконка меняется на галочку SuccessColor на ~1.2 с
            var feedback = BeginCopyFeedback(btn);
            if (feedback is null) return;

            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = System.TimeSpan.FromMilliseconds(1200)
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                EndCopyFeedback(btn, feedback);
            };
            timer.Start();
        }

        private sealed record CopyFeedbackState(object? Content, object? Tag);

        private static CopyFeedbackState? BeginCopyFeedback(Button button)
        {
            if (Equals(button.Tag, true)) return null;
            var state = new CopyFeedbackState(button.Content, button.Tag);
            button.Tag = true;
            var check = new Path
            {
                Width = 13,
                Height = 13,
                Stretch = Stretch.Uniform,
                Data = Geometry.Parse("M9 16.2 4.8 12l-1.4 1.4L9 19 21 7l-1.4-1.4z")
            };
            check.SetResourceReference(Shape.FillProperty, "SuccessColor");
            button.Content = check;
            return state;
        }

        private static void EndCopyFeedback(Button button, object? feedback)
        {
            if (feedback is not CopyFeedbackState state) return;
            button.Content = state.Content;
            button.Tag = state.Tag;
        }

        private void QuoteMessage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || !button.IsEnabled || button.DataContext is not ChatMessage msg) return;
            if (DataContext is not ChatViewModelBase vm) return;

            var text = ChatTurnLayout.CollectText(vm.Messages, msg);
            if (string.IsNullOrEmpty(text)) return;
            if (_selectedMessageBody?.TryGetTarget(out var body) == true &&
                !body.Selection.IsEmpty && body.DataContext is ChatMessage selected &&
                IsInTurn(vm, msg, selected))
            {
                var selection = body.Selection.Text.Trim();
                if (selection.Length > 0) text = selection;
            }
            ForgetSelection();
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var quoted = string.Join("\n", System.Linq.Enumerable.Select(lines, l => "> " + l)) + "\n\n";
            vm.InputText = quoted + vm.InputText;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                FindComposer()?.FocusInputAtEnd();
                var footer = FindAncestor<Grid>(button, grid => grid.Name == "TurnFooter");
                if (footer == null || !footer.IsVisible || !ThreadScroll.IsAncestorOf(footer)) return;
                // Use the viewport's origin, excluding the outer ScrollViewer padding.
                if (ThreadScroll.Template.FindName("PART_ScrollContentPresenter", ThreadScroll) is not ScrollContentPresenter viewport)
                    return;
                var bounds = footer.TransformToAncestor(viewport)
                    .TransformBounds(new Rect(footer.RenderSize));
                var overlap = bounds.Bottom + 8 - viewport.ActualHeight;
                if (overlap > 0)
                    ThreadScroll.ScrollToVerticalOffset(ThreadScroll.VerticalOffset + overlap);
            }), System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        private void MessageBody_SelectionChanged(object sender, RoutedEventArgs e)
        {
            if (sender is not RichTextBox body) return;
            if (!body.Selection.IsEmpty)
                _selectedMessageBody = new WeakReference<RichTextBox>(body);
            else if (_selectedMessageBody?.TryGetTarget(out var previous) == true && ReferenceEquals(body, previous))
                _selectedMessageBody = null;
        }

        private void MessageBody_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is not RichTextBox body ||
                _selectedMessageBody?.TryGetTarget(out var selected) != true || !ReferenceEquals(body, selected)) return;
            var button = e.NewFocus is DependencyObject target
                ? FindAncestor<Button>(target, candidate => candidate.Name == "QuoteButton") : null;
            if (button?.DataContext is ChatMessage member && body.DataContext is ChatMessage selection &&
                DataContext is ChatViewModelBase vm && IsInTurn(vm, member, selection)) return;
            ForgetSelection();
        }

        private void QuoteButton_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => ForgetSelection();

        private static bool IsInTurn(ChatViewModelBase vm, ChatMessage member, ChatMessage candidate)
        {
            var range = ChatTurnLayout.TurnRange(vm.Messages, member);
            if (range is null) return false;
            for (var index = range.Value.Start; index <= range.Value.End; index++)
                if (ReferenceEquals(vm.Messages[index], candidate)) return true;
            return false;
        }

        private void ForgetSelection()
        {
            var reference = _selectedMessageBody;
            _selectedMessageBody = null;
            if (reference?.TryGetTarget(out var body) == true && !body.Selection.IsEmpty)
                body.Selection.Select(body.Selection.Start, body.Selection.Start);
        }

        private ChatInputBar? FindComposer()
        {
            for (DependencyObject? parent = VisualTreeHelper.GetParent(this); parent != null;
                 parent = VisualTreeHelper.GetParent(parent))
                if (parent is Panel && FindDescendant<ChatInputBar>(parent) is { } composer) return composer;
            return null;
        }

        private static T? FindAncestor<T>(DependencyObject start, Func<T, bool> predicate) where T : DependencyObject
        {
            for (DependencyObject? current = start; current != null; current =
                 current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
                if (current is T match && predicate(match)) return match;
            return null;
        }

        private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent is T match) return match;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
                if (FindDescendant<T>(VisualTreeHelper.GetChild(parent, index)) is { } child) return child;
            return null;
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
