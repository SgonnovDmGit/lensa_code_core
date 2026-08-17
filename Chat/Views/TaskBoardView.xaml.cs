using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using LenSa.Core.Chat.Models;
using LenSa.Core.Chat.ViewModels;

namespace LenSa.Core.Chat.Views
{
    /// <summary>
    /// T-000082 (REQ-021): доска задач над тредом чата. Данные — ChatViewModelBase.TasksView
    /// (срез TaskBoard по режиму/фильтру/сортировке). Ведёт доску AI тулом task_board_update,
    /// но пользователь правит её и руками.
    ///
    /// Здесь — только перетаскивание строк (порядок задач): чистая UI-механика, в VM ей делать
    /// нечего. Сама перестановка — ChatViewModelBase.MoveTask.
    /// </summary>
    public partial class TaskBoardView : UserControl
    {
        /// <summary>
        /// Узкая колонка чата (открыта панель кода): доска прячет «наш №», трекер и ручку
        /// перетаскивания, оставляя задачу, статус и «принял» (вариант C мокапа 2026-07-12).
        /// </summary>
        public static readonly DependencyProperty IsCompactProperty =
            DependencyProperty.Register(nameof(IsCompact), typeof(bool), typeof(TaskBoardView),
                new PropertyMetadata(false));

        public bool IsCompact
        {
            get => (bool)GetValue(IsCompactProperty);
            private set => SetValue(IsCompactProperty, value);
        }

        // Порог: ниже него 7 колонок налезают друг на друга (доска + композер меряны на мокапе).
        private const double CompactBelow = 560;

        public TaskBoardView()
        {
            InitializeComponent();
            SizeChanged += (_, __) => ApplyCompact();
        }

        private void ApplyCompact()
        {
            var compact = ActualWidth > 0 && ActualWidth < CompactBelow;
            if (compact == IsCompact) return;

            IsCompact = compact;

            // Ширины колонок — общий источник для шапки и строк, поэтому схлопываем сам ресурс.
            Resources["ColDrag"] = new GridLength(compact ? 0 : 26);
            Resources["ColOurId"] = new GridLength(compact ? 0 : 58);
            Resources["ColTracker"] = new GridLength(compact ? 0 : 104);
        }

        // Визуал перетаскивания (T-000127): призрак строки под курсором + линия места вставки.
        // Голый DoDragDrop не показывал ни что тянем, ни куда упадёт.
        private DragGhostAdorner? _ghost;
        private InsertionLineAdorner? _line;
        private FrameworkElement? _lineRow;
        private FrameworkElement? _sourceRow;

        private void DragHandle_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement handle || handle.DataContext is not TaskItem item) return;

            _sourceRow = FindRow(handle);
            ShowGhost(_sourceRow);
            if (_sourceRow != null) _sourceRow.Opacity = 0.4;   // видно, откуда тянем

            try
            {
                DragDrop.DoDragDrop(handle, item, DragDropEffects.Move);
            }
            finally
            {
                ClearAdorners();
                if (_sourceRow != null) _sourceRow.Opacity = 1.0;
                _sourceRow = null;
            }
            e.Handled = true;
        }

        private void TaskRow_DragOver(object sender, DragEventArgs e)
        {
            var ok = e.Data.GetDataPresent(typeof(TaskItem));
            e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
            if (!ok || sender is not FrameworkElement row) return;

            _ghost?.SetPosition(e.GetPosition(this));

            // Верхняя половина строки — ляжет перед ней, нижняя — после
            ShowInsertionLine(row, InsertBefore(row, e));
        }

        private void TaskRow_Drop(object sender, DragEventArgs e)
        {
            ClearAdorners();

            if (sender is not FrameworkElement row || row.DataContext is not TaskItem target) return;
            if (e.Data.GetData(typeof(TaskItem)) is not TaskItem dragged) return;
            if (DataContext is not ChatViewModelBase vm) return;

            vm.MoveTask(dragged, target, InsertBefore(row, e));
            e.Handled = true;
        }

        private static bool InsertBefore(FrameworkElement row, DragEventArgs e) =>
            e.GetPosition(row).Y < row.ActualHeight / 2;

        private void ShowGhost(FrameworkElement? row)
        {
            if (row == null) return;
            var layer = AdornerLayer.GetAdornerLayer(this);
            if (layer == null) return;

            _ghost = new DragGhostAdorner(this, row);
            layer.Add(_ghost);
        }

        private void ShowInsertionLine(FrameworkElement row, bool atTop)
        {
            if (ReferenceEquals(_lineRow, row) && _line != null && _line.IsAtTop == atTop) return;

            RemoveLine();
            var layer = AdornerLayer.GetAdornerLayer(row);
            if (layer == null) return;

            _line = new InsertionLineAdorner(row, atTop);
            _lineRow = row;
            layer.Add(_line);
        }

        private void RemoveLine()
        {
            if (_line != null && _lineRow != null)
                AdornerLayer.GetAdornerLayer(_lineRow)?.Remove(_line);
            _line = null;
            _lineRow = null;
        }

        private void ClearAdorners()
        {
            RemoveLine();
            if (_ghost != null)
                AdornerLayer.GetAdornerLayer(this)?.Remove(_ghost);
            _ghost = null;
        }

        /// <summary>Border строки — родитель ручки ⠿ в визуальном дереве.</summary>
        private static FrameworkElement? FindRow(DependencyObject? child)
        {
            while (child != null)
            {
                child = System.Windows.Media.VisualTreeHelper.GetParent(child);
                if (child is Border b && b.DataContext is TaskItem) return b;
            }
            return null;
        }
    }
}
