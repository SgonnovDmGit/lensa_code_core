using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using LenSa.Core.Helpers;
using LenSa.Core.Services.Api;

namespace LenSa.Core.Chat.Views
{
    /// <summary>Catalog-backed selection UI. Selection policy and persistence belong to the view model.</summary>
    public partial class ReasoningSelector : UserControl
    {
        public static readonly DependencyProperty ModelProperty = DependencyProperty.Register(
            nameof(Model), typeof(ModelDto), typeof(ReasoningSelector), new PropertyMetadata(null, ModelChanged));
        public static readonly DependencyProperty SelectedEffortProperty = DependencyProperty.Register(
            nameof(SelectedEffort), typeof(string), typeof(ReasoningSelector),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, SelectionChanged));

        private IReadOnlyList<string> _levels = Array.Empty<string>();
        private readonly List<Button> _buttons = new();
        private bool _updating;

        public ModelDto? Model
        {
            get => (ModelDto?)GetValue(ModelProperty);
            set => SetValue(ModelProperty, value);
        }
        public string SelectedEffort
        {
            get => (string)GetValue(SelectedEffortProperty);
            set => SetValue(SelectedEffortProperty, value);
        }

        public ReasoningSelector()
        {
            InitializeComponent();
            Rebuild();
        }

        private static void ModelChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
            ((ReasoningSelector)sender).Rebuild();
        private static void SelectionChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) =>
            ((ReasoningSelector)sender).UpdateSelection();

        private void Rebuild()
        {
            if (ButtonsPanel is null) return;
            _levels = Model is null ? Array.Empty<string>() : ReasoningLevelPolicy.Normalize(Model.ReasoningEfforts);
            NoLevelsText.Visibility = _levels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SliderPanel.Visibility = _levels.Count >= 3 ? Visibility.Visible : Visibility.Collapsed;
            ButtonsPanel.Visibility = _levels.Count is 1 or 2 ? Visibility.Visible : Visibility.Collapsed;
            ButtonsPanel.Children.Clear();
            _buttons.Clear();
            _updating = true;
            try { LevelsSlider.Maximum = Math.Max(1, _levels.Count - 1); }
            finally { _updating = false; }

            if (_levels.Count is 1 or 2)
            {
                foreach (var token in _levels)
                {
                    var dot = new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 6, 0) };
                    dot.SetResourceReference(Shape.FillProperty, ReasoningNameConverter.BrushKeyFor(token));
                    var label = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                    SetLabel(label, token);
                    var content = new StackPanel { Orientation = Orientation.Horizontal };
                    content.Children.Add(dot);
                    content.Children.Add(label);
                    var button = new Button { Tag = token, Content = content, Style = (Style)FindResource("StepButton") };
                    button.Click += Step_Click;
                    _buttons.Add(button);
                    ButtonsPanel.Children.Add(button);
                }
            }
            RebuildTicks();
            UpdateSelection();
        }

        private void UpdateSelection()
        {
            if (SelectedLabel is null) return;
            _updating = true;
            try
            {
                var index = _levels.ToList().IndexOf(SelectedEffort);
                LevelsSlider.Value = Math.Max(0, index);
                SetLabel(SelectedLabel, SelectedEffort);
                SelectedLabel.SetResourceReference(TextBlock.ForegroundProperty, ReasoningNameConverter.BrushKeyFor(SelectedEffort));
                LevelsSlider.ApplyTemplate();
                if (LevelsSlider.Template.FindName("PART_Track", LevelsSlider) is Track track)
                    track.Thumb?.SetResourceReference(Control.BackgroundProperty, ReasoningNameConverter.BrushKeyFor(SelectedEffort));
                foreach (var button in _buttons)
                {
                    var selected = string.Equals(button.Tag as string, SelectedEffort, StringComparison.Ordinal);
                    if (selected)
                    {
                        button.SetResourceReference(Control.BackgroundProperty, "HoverBackground");
                        button.SetResourceReference(Control.BorderBrushProperty, "SecondaryForeground");
                    }
                    else
                    {
                        button.Background = Brushes.Transparent;
                        button.BorderBrush = Brushes.Transparent;
                    }
                    button.FontWeight = selected ? FontWeights.Bold : FontWeights.Normal;
                }
            }
            finally { _updating = false; }
        }

        private static void SetLabel(TextBlock label, string? token)
        {
            var key = ReasoningNameConverter.ResourceKeyFor(token);
            if (key is null) label.Text = token ?? string.Empty;
            else label.SetResourceReference(TextBlock.TextProperty, key);
        }

        private void RebuildTicks()
        {
            TicksPanel.Children.Clear();
            if (_levels.Count < 3) return;
            for (var i = 0; i < _levels.Count; i++)
            {
                var tick = new Ellipse { Width = 6, Height = 6 };
                tick.SetResourceReference(Shape.FillProperty, ReasoningNameConverter.BrushKeyFor(_levels[i]));
                Canvas.SetLeft(tick, i * TicksPanel.ActualWidth / (_levels.Count - 1) - 3);
                Canvas.SetTop(tick, 9);
                TicksPanel.Children.Add(tick);
            }
        }

        private void TicksPanel_SizeChanged(object sender, SizeChangedEventArgs e) => RebuildTicks();
        private void Step_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string token }) SetCurrentValue(SelectedEffortProperty, token);
        }
        private void LevelsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_updating || _levels.Count < 3) return;
            SetCurrentValue(SelectedEffortProperty, _levels[Math.Clamp((int)Math.Round(e.NewValue), 0, _levels.Count - 1)]);
        }
    }
}
