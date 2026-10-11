using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using LenSa.Core.Chat.Models;
using LenSa.Core.Helpers;

namespace LenSa.Core.Chat.Views
{
    /// <summary>Presentation of the rows selected by MetricCardBuilder.</summary>
    public partial class MetricCardView : UserControl
    {
        public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
            nameof(Message), typeof(ChatMessage), typeof(MetricCardView), new PropertyMetadata(null, MessageChanged));
        public ChatMessage? Message
        {
            get => (ChatMessage?)GetValue(MessageProperty);
            set => SetValue(MessageProperty, value);
        }
        public MetricCardData? Data { get; private set; }

        public MetricCardView() { InitializeComponent(); }
        private static void MessageChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
            ((MetricCardView)sender).Refresh();

        public void Refresh()
        {
            CardBody.Children.Clear();
            Data = Message is null ? null : MetricCardBuilder.Build(Message, CultureInfo.CurrentCulture);
            if (Data is null) return;

            var header = TwoColumns(Label(Data.TitleKey, 13, false),
                Data.CoinsText is string coins ? Value(coins, "str_MetricCoinsUnit", 12, "AccentColor") : null);
            header.Margin = new Thickness(13, 11, 13, 3);
            foreach (var child in header.Children)
                if (child is TextBlock text) text.FontWeight = FontWeights.Bold;
            CardBody.Children.Add(header);
            if (!string.IsNullOrEmpty(Data.ModelName))
            {
                var model = Value(Data.ModelName, null, 11.5);
                model.FontWeight = FontWeights.SemiBold;
                model.Margin = new Thickness(13, 1, 13, 0);
                model.TextWrapping = TextWrapping.Wrap;
                CardBody.Children.Add(model);
            }

            FrameworkElement? reasoning = null;
            if (Data.ReasoningEffort is string effort)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 5, 0) };
                dot.SetResourceReference(Shape.FillProperty, ReasoningNameConverter.BrushKeyFor(effort));
                row.Children.Add(dot);
                var text = new TextBlock { FontSize = 10.5, FontWeight = FontWeights.SemiBold };
                text.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryForeground");
                var prefix = new Run();
                prefix.SetResourceReference(Run.TextProperty, "str_MetricReasoning");
                text.Inlines.Add(prefix);
                var name = new Run();
                if (ReasoningNameConverter.ResourceKeyFor(effort) is string key)
                    name.SetResourceReference(Run.TextProperty, key);
                else name.Text = effort;
                text.Inlines.Add(name);
                row.Children.Add(text);
                reasoning = row;
            }
            else if (Message?.IsCheckpoint == true && Message.CheckpointTrigger is "auto" or "manual")
            {
                reasoning = Label(Message.CheckpointTrigger == "auto"
                    ? "str_Chat_CheckpointAuto" : "str_Chat_CheckpointManual", 10.5);
            }
            if (reasoning is not null || Data.DateText is not null)
            {
                var sub = TwoColumns(reasoning,
                    Data.DateText is string date ? Value(date, null, 10.5, "SecondaryForeground") : null);
                sub.Margin = new Thickness(13, 3, 13, 10);
                CardBody.Children.Add(sub);
            }

            if (Data.Context is { } context)
            {
                var body = Section(null);
                var value = Value(context.TokensText, null, 11);
                if (context.LimitText is string limit)
                {
                    value.Inlines.Add(new Run(" "));
                    var of = new Run();
                    of.SetResourceReference(Run.TextProperty, "str_MetricOf");
                    value.Inlines.Add(of);
                    value.Inlines.Add(new Run($" {limit} · {context.PercentText}%"));
                }
                body.Children.Add(TwoColumns(Label("str_MetricContextAfter", 11), value));
                if (context.LimitText is not null)
                {
                    var zone = (string)new ContextZoneConverter().Convert(context.Fill * 100,
                        typeof(string), null!, CultureInfo.CurrentCulture);
                    body.Children.Add(Progress(context.Fill,
                        zone == "crit" ? "ErrorColor" : zone == "warn" ? "ReasoningForeground" : "AccentColor"));
                }
            }
            if (Data.WindowRows.Count > 0)
            {
                var body = Section("str_MetricWindow");
                foreach (var row in Data.WindowRows) body.Children.Add(Row(row));
                var hint = Label("str_MetricWindowHint", 10.5);
                hint.TextWrapping = TextWrapping.Wrap;
                hint.Margin = new Thickness(0, 4, 0, 0);
                body.Children.Add(hint);
            }
            if (Data.TokenRows.Count > 0)
            {
                var body = Section("str_MetricTokens");
                foreach (var row in Data.TokenRows)
                {
                    body.Children.Add(Row(row));
                    if (row.LabelKey == "str_MetricIncoming" && Data.CacheSplit is { } split)
                    {
                        body.Children.Add(Progress(split.Fill, "AccentColor"));
                        var cacheLegend = Legend("str_MetricCached", split.CachedText,
                            (split.Fill * 100).ToString("F0", CultureInfo.CurrentCulture), "AccentColor");
                        var newLegend = Legend("str_MetricNew", split.NewText,
                            (100 - Math.Round(split.Fill * 100)).ToString("F0", CultureInfo.CurrentCulture), "TertiaryBackground");
                        var legends = TwoColumns(cacheLegend, newLegend);
                        legends.Margin = new Thickness(0, 0, 0, 3);
                        body.Children.Add(legends);
                    }
                }
            }
            if (Data.TimeRows.Count > 0)
            {
                var body = Section("str_MetricTime");
                foreach (var row in Data.TimeRows) body.Children.Add(Row(row));
            }
        }

        private StackPanel Section(string? titleKey)
        {
            var body = new StackPanel();
            var border = new Border { BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(13, 9, 13, 10), Child = body };
            border.SetResourceReference(Border.BorderBrushProperty, "BorderColor");
            CardBody.Children.Add(border);
            if (titleKey is not null)
            {
                var title = Label(titleKey, 10.5, false);
                title.FontWeight = FontWeights.Bold;
                var accent = new Border { BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(6, 1, 0, 1), Margin = new Thickness(0, 0, 0, 5), Child = title };
                accent.SetResourceReference(Border.BorderBrushProperty, "AccentColor");
                body.Children.Add(accent);
            }
            return body;
        }

        private static Grid Row(MetricRow data)
        {
            var row = TwoColumns(Label(data.LabelKey, 11), Value(data.Value, data.UnitKey, 11));
            row.Tag = data.LabelKey;
            row.Margin = new Thickness(0, 2.5, 0, 2.5);
            return row;
        }
        private static Grid TwoColumns(FrameworkElement? left, FrameworkElement? right)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (left is not null) grid.Children.Add(left);
            if (right is not null)
            {
                right.Margin = new Thickness(8, 0, 0, 0);
                right.HorizontalAlignment = HorizontalAlignment.Right;
                Grid.SetColumn(right, 1);
                grid.Children.Add(right);
            }
            return grid;
        }

        private static FrameworkElement Progress(double fill, string brushKey)
        {
            var track = new Border { Height = 7, CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1), Margin = new Thickness(0, 4, 0, 5), ClipToBounds = true };
            track.SetResourceReference(Border.BackgroundProperty, "TertiaryBackground");
            track.SetResourceReference(Border.BorderBrushProperty, "BorderColor");
            var grid = new Grid();
            var clamped = Math.Clamp(fill, 0, 1);
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(clamped, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - clamped, GridUnitType.Star) });
            var bar = new Border { Tag = "MetricProgressFill" };
            bar.SetResourceReference(Border.BackgroundProperty, brushKey);
            grid.Children.Add(bar);
            track.Child = grid;
            return track;
        }
        private static StackPanel Legend(string labelKey, string value, string percent, string brushKey)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var dot = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(1),
                BorderThickness = new Thickness(0.5), Margin = new Thickness(0, 0, 4, 0) };
            dot.SetResourceReference(Border.BackgroundProperty, brushKey);
            dot.SetResourceReference(Border.BorderBrushProperty, "BorderColor");
            row.Children.Add(dot);
            var label = Label(labelKey, 10.5);
            label.Inlines.Add(new Run($" {percent}% · {value}"));
            row.Children.Add(label);
            return row;
        }
        internal static TextBlock Label(string resourceKey, double size, bool secondary = true)
        {
            var text = new TextBlock { FontSize = size, Tag = resourceKey };
            var label = new Run();
            label.SetResourceReference(Run.TextProperty, resourceKey);
            text.Inlines.Add(label);
            text.SetResourceReference(TextBlock.ForegroundProperty, secondary ? "SecondaryForeground" : "PrimaryForeground");
            return text;
        }
        internal static TextBlock Value(string value, string? unit, double size, string brush = "PrimaryForeground")
        {
            var text = new TextBlock { FontSize = size, FontWeight = FontWeights.SemiBold };
            text.SetResourceReference(TextBlock.ForegroundProperty, brush);
            text.Inlines.Add(new Run(value));
            if (unit is not null)
            {
                text.Inlines.Add(new Run(" "));
                var suffix = new Run();
                suffix.SetResourceReference(Run.TextProperty, unit);
                text.Inlines.Add(suffix);
            }
            return text;
        }
    }
}
