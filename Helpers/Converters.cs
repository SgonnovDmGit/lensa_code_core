using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using LenSa.Core.Services.Theme;

namespace LenSa.Core.Helpers
{
    /// <summary>
    /// Конвертер AppTheme → локализованная строка (str_ThemeLight / str_ThemeDark / str_Theme1C).
    /// Локализационные ключи живут в продуктовой сборке, поэтому используется Application.Current.FindResource.
    /// </summary>
    public class AppThemeToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is AppTheme theme)
            {
                var key = theme switch
                {
                    AppTheme.Light => "str_ThemeLight",
                    AppTheme.Dark => "str_ThemeDark",
                    AppTheme.OneC => "str_Theme1C",
                    _ => "str_ThemeLight"
                };
                return Application.Current.FindResource(key) as string ?? theme.ToString();
            }
            return value?.ToString() ?? string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }


    /// <summary>
    /// Имя тула → человекочитаемое локализованное имя (T-000102).
    /// Ищет ключ str_Tool_&lt;имя&gt; в ресурсах приложения (словарь живёт в продуктовой
    /// сборке); нет ключа — показывает сырое имя. ConverterParameter не используется.
    /// </summary>
    public class ToolDisplayNameConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string name || string.IsNullOrEmpty(name))
                return value ?? string.Empty;

            return Application.Current?.TryFindResource("str_Tool_" + name) as string ?? name;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>Инпут тула → Visibility: null / пусто / "{}" / "null" → Collapsed
    /// (тулы без параметров не показывают пустой JSON в чате).</summary>
    public class ToolInputToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = (value as string)?.Trim();
            return string.IsNullOrEmpty(s) || s == "{}" || s == "null"
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is true ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is Visibility.Visible;
        }
    }

    public class InvertBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is true ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is Visibility.Collapsed;
        }
    }

    public class InvertBoolConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is true ? false : true;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is true ? false : true;
        }
    }

    public class StringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>Обратный StringToVisibilityConverter — пустое → Visible (для placeholder-текста в TextBox).
    /// ConverterParameter="invert" переворачивает: непустое → Visible (для контентных блоков, которые
    /// прячутся когда строка пустая).</summary>
    public class EmptyStringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var isEmpty = string.IsNullOrEmpty(value as string);
            var invert = string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase);
            if (invert)
                return isEmpty ? Visibility.Collapsed : Visibility.Visible;
            return isEmpty ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>Nullable collection → Visibility: null или пустая → Collapsed, иначе Visible.</summary>
    public class CollectionToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is System.Collections.ICollection c && c.Count > 0) return Visibility.Visible;
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class WidthRatioConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not double available || double.IsNaN(available) || available <= 0)
                return double.PositiveInfinity;

            var ratio = 0.7;
            if (parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
                ratio = p;
            else if (parameter is double pd)
                ratio = pd;

            var result = available * ratio;
            if (result < 400) result = 400;
            return result;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class RowIndexConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is DataGridRow row)
                return row.GetIndex() + 1;
            return 0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>null → true. Для триггеров «элемент вне выпадашки» (нет предка ComboBoxItem).</summary>
    public class IsNullConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value == null;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>Первая буква строки (upper-case) — буква-аватар свёрнутой рейки сайдбара (T-000111).</summary>
    public class FirstLetterConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var s = (value as string)?.Trim();
            return string.IsNullOrEmpty(s) ? "?" : s[..1].ToUpperInvariant();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Процент (0–100) → дуга кольца контекста (T-000074). Кольцо 27×27, r=10.5,
    /// старт сверху, по часовой. 0% → пустая геометрия, ≥99.5% → полное кольцо.
    /// </summary>
    public class PercentToArcConverter : IValueConverter
    {
        // Геометрия кольца контекста. Должна совпадать с фоновым Ellipse в ChatInputBar.xaml —
        // это ОДНО кольцо, нарисованное двумя примитивами, и любое расхождение видно глазом.
        //
        // Держатся вместе так (сетка 24×24, штрих 3.2):
        //   центр   = 24 / 2                      = 12
        //   радиус  = 10  (осевая линия штриха)
        //   Ellipse = 2*R + StrokeThickness = 23.2  ← его Width/Height в XAML
        //   габарит = 12 ± (10 + 3.2/2) = 0.4..23.6 — влезает в 24 без обрезки
        //
        // Было Cx=Cy=13.5, R=10.5 — расчёт под сетку 27×27. Кольцо потом ужали до 24,
        // фоновый Ellipse подогнали, а конвертер забыли: дуга оказалась крупнее фона на
        // 1.35 px и смещена на 1.5 px по обеим осям (T-000165). Меняешь размер кольца —
        // меняй ОБА места, иначе дефект вернётся.
        private const double Cx = 12.0, Cy = 12.0, R = 10.0;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var pct = value switch
            {
                double d => d,
                int i => i,
                _ => 0.0
            };
            if (pct <= 0) return Geometry.Empty;
            if (pct >= 99.5)
                return new EllipseGeometry(new System.Windows.Point(Cx, Cy), R, R);

            var angle = pct / 100.0 * 360.0;
            var rad = (angle - 90.0) * Math.PI / 180.0;
            var start = new System.Windows.Point(Cx, Cy - R);
            var end = new System.Windows.Point(Cx + R * Math.Cos(rad), Cy + R * Math.Sin(rad));

            var fig = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
            fig.Segments.Add(new ArcSegment(end, new System.Windows.Size(R, R), 0,
                isLargeArc: angle > 180, SweepDirection.Clockwise, isStroked: true));
            var geo = new PathGeometry();
            geo.Figures.Add(fig);
            geo.Freeze();
            return geo;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Зона заполнения контекста: &lt;70 → "ok", ≥70 → "warn", ≥90 → "crit".
    /// Одно правило для кольца, тултипа и полосы карточки метрики (D26a/D51).
    /// </summary>
    public class ContextZoneConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var pct = value switch { double d => d, int i => i, _ => 0.0 };
            pct = Math.Round(pct, MidpointRounding.AwayFromZero);
            return pct >= 90 ? "crit" : pct >= 70 ? "warn" : "ok";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// [0] contextLength модели, [1] токены текущего контекста → влезает ли диалог
    /// в окно модели. false ⇒ пункт списка моделей дизейблится (T-000074).
    /// </summary>
    /// <summary>
    /// Отступ шапки таблицы под вертикальный скроллбар ленты (замер 12.07).
    /// Шапка живёт вне ScrollViewer и про скроллбар не знает: он съедает у строк ~17px,
    /// и всё правее звёздной колонки уезжает относительно заголовков. Отдаём разницу
    /// ActualWidth − ViewportWidth как правый Margin — скроллбара нет, значит и отступа нет.
    /// </summary>
    public class ScrollBarGapConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2 ||
                values[0] is not double actual || values[1] is not double viewport)
                return new Thickness(0);

            var gap = actual - viewport;
            return new Thickness(0, 0, gap > 0 ? gap : 0, 0);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    public class ModelFitsContextConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2) return true;
            var ctxLen = ToInt(values[0]);
            var used = ToInt(values[1]);
            if (ctxLen <= 0 || used <= 0) return true;
            return used < ctxLen;
        }

        private static int ToInt(object v) => v switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            _ => 0
        };

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    /// <summary>
    /// Уровень размышлений ↔ позиция ползунка. Шкала — серверный контракт REQ-007,
    /// а не выдуманная: off | minimal | low | medium | high.
    /// </summary>
    public class ReasoningIndexConverter : IValueConverter
    {
        private static readonly string[] Levels = { "off", "minimal", "low", "medium", "high" };

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var idx = Array.IndexOf(Levels, value as string ?? "off");
            return (double)(idx < 0 ? 0 : idx);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var idx = value switch { double d => (int)Math.Round(d), int i => i, _ => 0 };
            return Levels[Math.Clamp(idx, 0, Levels.Length - 1)];
        }
    }

    /// <summary>Maps known reasoning tokens to localized labels and theme brushes.</summary>
    public class ReasoningNameConverter : IValueConverter
    {
        public static string? ResourceKeyFor(string? token) => token?.Trim().ToLowerInvariant() switch
        {
            "off" or "none" => "str_ReasoningLevelNone",
            "minimal" => "str_ReasoningLevelMinimal",
            "low" => "str_ReasoningLevelLow",
            "medium" => "str_ReasoningLevelMedium",
            "high" => "str_ReasoningLevelHigh",
            "xhigh" => "str_ReasoningLevelXHigh",
            "max" => "str_ReasoningLevelMax",
            _ => null
        };

        public static string BrushKeyFor(string? token) => token?.Trim().ToLowerInvariant() switch
        {
            "off" or "none" => "ReasoningOff",
            "minimal" => "ReasoningMinimal",
            "low" => "ReasoningLow",
            "medium" => "ReasoningMedium",
            "high" => "ReasoningHigh",
            "xhigh" => "ReasoningXHigh",
            "max" => "ReasoningMax",
            _ => "SecondaryForeground"
        };

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var token = value as string;
            var key = ResourceKeyFor(token);
            return key is null
                ? token ?? string.Empty
                : Application.Current?.TryFindResource(key) as string ?? token!;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }

    /// <summary>Режим → подпись чипа. Берётся из словаря локализации, чтобы не хардкодить язык.</summary>
    public class AssistModeNameConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var key = (value as string) switch
            {
                "planning" => "str_ModePlanning",
                "review" => "str_ModeReview",
                _ => "str_ModeAuto"
            };
            return Application.Current?.TryFindResource(key) as string ?? key;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
