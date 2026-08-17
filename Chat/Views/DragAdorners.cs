using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LenSa.Core.Chat.Views
{
    /// <summary>
    /// «Призрак» перетаскиваемой строки под курсором (T-000127). Раньше drag&amp;drop был голый:
    /// строка не подсвечивалась, и было не видно, что именно тянешь.
    /// </summary>
    public sealed class DragGhostAdorner : Adorner
    {
        private readonly Rectangle _ghost;
        private Point _position;

        public DragGhostAdorner(UIElement adornedElement, FrameworkElement source)
            : base(adornedElement)
        {
            IsHitTestVisible = false;

            // Призрак кладётся на непрозрачную подложку темы: голый VisualBrush поверх пёстрого
            // фона читался бледно (смок 12.07).
            var backdrop = Application.Current?.TryFindResource("SecondaryBackground") as Brush
                           ?? Brushes.White;

            _ghost = new Rectangle
            {
                Width = source.ActualWidth,
                Height = source.ActualHeight,
                Fill = new DrawingBrush(new DrawingGroup
                {
                    Children =
                    {
                        new GeometryDrawing(backdrop, null,
                            new RectangleGeometry(new Rect(0, 0, 1, 1))),
                        new GeometryDrawing(new VisualBrush(source) { Opacity = 0.95 }, null,
                            new RectangleGeometry(new Rect(0, 0, 1, 1)))
                    }
                })
                { Opacity = 0.92 },
                Stroke = Application.Current?.TryFindResource("AccentColor") as Brush ?? Brushes.DodgerBlue,
                StrokeThickness = 1,
                RadiusX = 4,
                RadiusY = 4,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 10,
                    ShadowDepth = 3,
                    Opacity = 0.45
                }
            };
            AddVisualChild(_ghost);
        }

        /// <summary>Курсор поехал — двигаем призрак (координаты в системе adorned-элемента).</summary>
        public void SetPosition(Point p)
        {
            // Небольшой сдвиг от курсора, чтобы призрак не перекрывал точку броска
            _position = new Point(p.X - 16, p.Y - 10);
            (Parent as AdornerLayer)?.Update(AdornedElement);
        }

        protected override int VisualChildrenCount => 1;
        protected override Visual GetVisualChild(int index) => _ghost;

        protected override Size MeasureOverride(Size constraint)
        {
            _ghost.Measure(constraint);
            return _ghost.DesiredSize;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            _ghost.Arrange(new Rect(_position, new Size(_ghost.Width, _ghost.Height)));
            return finalSize;
        }
    }

    /// <summary>
    /// Линия места вставки: рисуется по верхней или нижней грани строки, над которой курсор.
    /// Показывает, КУДА ляжет задача, а не просто «сюда можно бросить».
    /// </summary>
    public sealed class InsertionLineAdorner : Adorner
    {
        private readonly bool _atTop;
        private readonly Brush _brush;

        /// <summary>Линия у верхней грани строки (иначе — у нижней).</summary>
        public bool IsAtTop => _atTop;

        public InsertionLineAdorner(UIElement adornedElement, bool atTop) : base(adornedElement)
        {
            IsHitTestVisible = false;
            _atTop = atTop;
            _brush = Application.Current?.TryFindResource("AccentColor") as Brush ?? Brushes.DodgerBlue;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var w = AdornedElement.RenderSize.Width;
            var y = _atTop ? 1 : AdornedElement.RenderSize.Height - 1;

            var pen = new Pen(_brush, 2);
            dc.DrawLine(pen, new Point(0, y), new Point(w, y));
            // Кружок на левом конце — как каретка: видно, что это линия вставки, а не рамка
            dc.DrawEllipse(_brush, null, new Point(3, y), 3, 3);
        }
    }
}
