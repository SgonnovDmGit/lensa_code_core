using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace LenSa.Core.Helpers
{
    /// <summary>
    /// Лёгкий markdown → FlowDocument рендер для сообщений чата (T-000103).
    /// Блоки: заголовки #–######, fenced-код ```, списки -/*/1., цитаты >, ---.
    /// Инлайн: **жирный**, *курсив*, `код`. Кисти — через SetResourceReference,
    /// чтобы работала живая смена темы.
    /// </summary>
    public static class MarkdownRenderer
    {
        public static FlowDocument Render(string? markdown) => Render(markdown, null);

        /// <param name="codeBlockCommand">
        /// Команда «положить фрагмент в песочницу» (T-000096). Если задана — у каждого
        /// код-блока появляется кнопка, параметр команды = текст блока. null (Universal,
        /// где песочницы нет) — блоки рендерятся как раньше.
        /// </param>
        public static FlowDocument Render(string? markdown, ICommand? codeBlockCommand)
        {
            var doc = new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontSize = 13,
                LineHeight = 19,
                // Fallback-цепочка: Segoe UI не содержит эмодзи, и ⏳/✅ из ответа модели
                // рисовались квадратиками-крокозябрами (смок 12.07).
                FontFamily = UiFontWithEmoji
            };
            doc.SetResourceReference(TextElement.ForegroundProperty, "PrimaryForeground");

            if (string.IsNullOrEmpty(markdown))
                return doc;

            var lines = markdown.Replace("\r\n", "\n").Split('\n');
            var paragraphBuffer = new List<string>();

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();

                // Fenced code block
                if (trimmed.StartsWith("```"))
                {
                    FlushParagraph(doc, paragraphBuffer);
                    var codeLines = new List<string>();
                    i++;
                    while (i < lines.Length && !lines[i].TrimStart().StartsWith("```"))
                    {
                        codeLines.Add(lines[i]);
                        i++;
                    }
                    var code = string.Join("\n", codeLines);
                    doc.Blocks.Add(BuildCodeBlockWithActions(code, codeBlockCommand));
                    continue;
                }

                // Пустая строка — разрыв параграфа
                if (trimmed.Length == 0)
                {
                    FlushParagraph(doc, paragraphBuffer);
                    continue;
                }

                // Заголовок
                var heading = Regex.Match(trimmed, @"^(#{1,6})\s+(.*)$");
                if (heading.Success)
                {
                    FlushParagraph(doc, paragraphBuffer);
                    doc.Blocks.Add(BuildHeading(heading.Groups[2].Value, heading.Groups[1].Value.Length));
                    continue;
                }

                // Горизонтальная линия
                if (Regex.IsMatch(trimmed, @"^(-{3,}|\*{3,}|_{3,})$"))
                {
                    FlushParagraph(doc, paragraphBuffer);
                    doc.Blocks.Add(BuildRule());
                    continue;
                }

                // Таблица GFM: строка с пайпами + следующая строка-разделитель |---|---|
                if (IsTableStart(lines, i))
                {
                    FlushParagraph(doc, paragraphBuffer);
                    doc.Blocks.Add(BuildTable(lines, ref i));
                    continue;
                }

                // Список (маркированный / нумерованный) — собираем все подряд идущие пункты
                if (IsListItem(trimmed, out _, out _))
                {
                    FlushParagraph(doc, paragraphBuffer);
                    var list = BuildList(lines, ref i);
                    doc.Blocks.Add(list);
                    continue;
                }

                // Цитата
                if (trimmed.StartsWith(">"))
                {
                    FlushParagraph(doc, paragraphBuffer);
                    var quoteLines = new List<string> { trimmed.TrimStart('>').TrimStart() };
                    while (i + 1 < lines.Length && lines[i + 1].TrimStart().StartsWith(">"))
                    {
                        i++;
                        quoteLines.Add(lines[i].TrimStart().TrimStart('>').TrimStart());
                    }
                    doc.Blocks.Add(BuildQuote(quoteLines));
                    continue;
                }

                paragraphBuffer.Add(line);
            }

            FlushParagraph(doc, paragraphBuffer);
            return doc;
        }

        /// <summary>Шрифт UI с эмодзи-фолбэком: Segoe UI не содержит ⏳/✅ и рисует квадратики.</summary>
        private static readonly FontFamily UiFontWithEmoji =
            new("Segoe UI, Segoe UI Emoji, Segoe UI Symbol");

        // --- Таблицы (GFM) --------------------------------------------------

        /// <summary>Заголовок таблицы + строка-разделитель под ним: |---|:--:|---|</summary>
        private static bool IsTableStart(string[] lines, int i)
        {
            if (i + 1 >= lines.Length) return false;
            if (!lines[i].Contains('|')) return false;
            return Regex.IsMatch(lines[i + 1].Trim(), @"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?$");
        }

        private static string[] SplitRow(string line) =>
            line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();

        private static Table BuildTable(string[] lines, ref int i)
        {
            var header = SplitRow(lines[i]);
            i++; // строка-разделитель — её саму не рисуем
            var rows = new List<string[]>();

            while (i + 1 < lines.Length && lines[i + 1].Contains('|') &&
                   lines[i + 1].Trim().Length > 0)
            {
                i++;
                rows.Add(SplitRow(lines[i]));
            }

            var table = new Table
            {
                CellSpacing = 0,
                Margin = new Thickness(0, 4, 0, 8),
                FontSize = 12
            };
            for (var c = 0; c < header.Length; c++)
                table.Columns.Add(new TableColumn());

            var group = new TableRowGroup();
            table.RowGroups.Add(group);
            group.Rows.Add(BuildTableRow(header, isHeader: true, header.Length));
            foreach (var r in rows)
                group.Rows.Add(BuildTableRow(r, isHeader: false, header.Length));

            return table;
        }

        private static TableRow BuildTableRow(string[] cells, bool isHeader, int columnCount)
        {
            var row = new TableRow();
            if (isHeader) row.FontWeight = FontWeights.SemiBold;

            for (var c = 0; c < columnCount; c++)
            {
                var para = new Paragraph { Margin = new Thickness(0) };
                if (c < cells.Length) AppendInlines(para.Inlines, cells[c]);

                var cell = new TableCell(para)
                {
                    Padding = new Thickness(7, 4, 7, 4),
                    BorderThickness = new Thickness(0, 0, 0, 1)
                };
                cell.SetResourceReference(TableCell.BorderBrushProperty, "BorderColor");
                if (isHeader)
                    cell.SetResourceReference(TableCell.BackgroundProperty, "TertiaryBackground");

                row.Cells.Add(cell);
            }
            return row;
        }

        // --- Блоки ----------------------------------------------------------

        private static void FlushParagraph(FlowDocument doc, List<string> buffer)
        {
            if (buffer.Count == 0) return;

            var para = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
            for (int j = 0; j < buffer.Count; j++)
            {
                if (j > 0) para.Inlines.Add(new LineBreak());
                AppendInlines(para.Inlines, buffer[j]);
            }
            doc.Blocks.Add(para);
            buffer.Clear();
        }

        private static Paragraph BuildHeading(string text, int level)
        {
            var para = new Paragraph
            {
                FontWeight = FontWeights.SemiBold,
                FontSize = level switch { 1 => 17, 2 => 16, 3 => 14.5, _ => 13.5 },
                Margin = new Thickness(0, level <= 2 ? 8 : 6, 0, 4)
            };
            AppendInlines(para.Inlines, text);
            return para;
        }

        private static Paragraph BuildCodeBlock(string code)
        {
            var para = new Paragraph(new Run(code))
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Margin = new Thickness(0, 2, 0, 8),
                Padding = new Thickness(10, 8, 10, 8),
                BorderThickness = new Thickness(2, 0, 0, 0),
                LineHeight = 16
            };
            para.SetResourceReference(TextElement.BackgroundProperty, "CodeBackground");
            para.SetResourceReference(Paragraph.BorderBrushProperty, "CodeBorder");
            return para;
        }

        /// <summary>
        /// Код-блок с кнопками «Копировать» и «→ в песочницу» (T-000096). Код лежит в read-only
        /// TextBox: так он выделяется отдельно от текста ответа и живёт рядом с кнопками.
        /// «Копировать» есть всегда; «в песочницу» — только если продукт дал команду.
        /// </summary>
        /// <summary>Диагностический хук (продукт цепляет к файловому логу). null — no-op.</summary>
        public static Action<string>? DiagLog;

        private static BlockUIContainer BuildCodeBlockWithActions(string code, ICommand? sandboxCommand)
        {
            DiagLog?.Invoke($"codeblock: len={code.Length} sandboxCommand={(sandboxCommand == null ? "NULL" : "ok")}");

            var codeBox = new TextBox
            {
                Text = code,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = System.Windows.Media.Brushes.Transparent,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Padding = new Thickness(10, 8, 10, 8),
                TextWrapping = TextWrapping.NoWrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 320
            };
            codeBox.SetResourceReference(Control.ForegroundProperty, "PrimaryForeground");

            var bar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 6, 0)
            };

            bar.Children.Add(MakeCopyButton(code));

            if (sandboxCommand != null)
                bar.Children.Add(MakeCodeButton(
                    FindString("str_Chat_ToSandbox", "→ в песочницу"),
                    FindString("str_Chat_ToSandboxHint", "Положить фрагмент в песочницу"),
                    () =>
                    {
                        var can = sandboxCommand.CanExecute(code);
                        DiagLog?.Invoke($"click «в песочницу»: canExecute={can}");
                        if (can) sandboxCommand.Execute(code);
                    }));

            var grid = new Grid();
            grid.Children.Add(codeBox);
            grid.Children.Add(bar);

            var border = new Border
            {
                BorderThickness = new Thickness(2, 0, 0, 0),
                Margin = new Thickness(0, 2, 0, 8),
                Child = grid
            };
            border.SetResourceReference(Border.BackgroundProperty, "CodeBackground");
            border.SetResourceReference(Border.BorderBrushProperty, "CodeBorder");

            return new BlockUIContainer(border) { Margin = new Thickness(0) };
        }

        // Иконки: тот же контур, что у кнопки копирования под сообщением — глаз к нему уже привык
        private const string CopyIconPath =
            "M8 3h11a1 1 0 0 1 1 1v13h-2V5H8V3zM4 7h11a1 1 0 0 1 1 1v12a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V8a1 1 0 0 1 1-1zm1 2v10h9V9H5z";
        private const string CheckIconPath = "M9 16.2 4.8 12l-1.4 1.4L9 19 21 7l-1.4-1.4z";

        /// <summary>
        /// Копирование фрагмента: иконка вместо подписи, после клика — галочка на 30 секунд,
        /// чтобы было видно, что скопировалось (просьба юзера 12.07).
        /// </summary>
        private static Button MakeCopyButton(string code)
        {
            System.Windows.Threading.DispatcherTimer? timer = null;
            Button? button = null;

            // ВАЖНО: обработчик ровно один. Второй += на том же элементе не вызовется —
            // первый уже пометил событие Handled (из-за этого галочка не появлялась, 12.07).
            button = MakeCodeButton(
                MakeIcon(CopyIconPath, "SecondaryForeground"),
                FindString("str_Chat_CopyCodeHint", "Скопировать фрагмент в буфер обмена"),
                () =>
                {
                    try { Clipboard.SetText(code); }
                    catch { return; /* буфер занят другим процессом */ }

                    button!.Content = MakeIcon(CheckIconPath, "SuccessColor");

                    timer?.Stop();
                    timer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromSeconds(30)
                    };
                    timer.Tick += (_, _) =>
                    {
                        timer!.Stop();
                        button!.Content = MakeIcon(CopyIconPath, "SecondaryForeground");
                    };
                    timer.Start();
                });

            return button;
        }

        private static System.Windows.Shapes.Path MakeIcon(string data, string brushKey)
        {
            var path = new System.Windows.Shapes.Path
            {
                Width = 13,
                Height = 13,
                Stretch = Stretch.Uniform,
                Data = Geometry.Parse(data)
            };
            path.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brushKey);
            return path;
        }

        /// <summary>
        /// Кнопка внутри код-блока. Действие висит на PreviewMouseLeftButtonDown, а не на Click:
        /// в read-only RichTextBox первый клик забирает слой выделения текста (ставит каретку),
        /// и до кнопки он не доходит — она срабатывала лишь со второго раза (замер 12.07).
        /// </summary>
        private static Button MakeCodeButton(object? content, string tooltip, Action action)
        {
            var button = new Button
            {
                Content = content,
                ToolTip = tooltip,
                FontSize = 10,
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(6, 0, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                BorderThickness = new Thickness(1),
                Focusable = false
            };
            button.SetResourceReference(Control.BackgroundProperty, "SecondaryBackground");
            button.SetResourceReference(Control.BorderBrushProperty, "BorderColor");
            button.SetResourceReference(Control.ForegroundProperty, "AccentColor");

            button.PreviewMouseLeftButtonDown += (_, e) =>
            {
                action();
                e.Handled = true;
            };
            return button;
        }

        private static string FindString(string key, string fallback) =>
            Application.Current?.TryFindResource(key) as string ?? fallback;

        private static Paragraph BuildRule()
        {
            var para = new Paragraph
            {
                Margin = new Thickness(0, 4, 0, 8),
                BorderThickness = new Thickness(0, 0, 0, 1),
                FontSize = 1
            };
            para.SetResourceReference(Paragraph.BorderBrushProperty, "BorderColor");
            return para;
        }

        private static Paragraph BuildQuote(List<string> quoteLines)
        {
            var para = new Paragraph
            {
                Margin = new Thickness(0, 2, 0, 8),
                Padding = new Thickness(10, 4, 0, 4),
                BorderThickness = new Thickness(3, 0, 0, 0)
            };
            para.SetResourceReference(Paragraph.BorderBrushProperty, "BorderColor");
            para.SetResourceReference(TextElement.ForegroundProperty, "SecondaryForeground");
            for (int j = 0; j < quoteLines.Count; j++)
            {
                if (j > 0) para.Inlines.Add(new LineBreak());
                AppendInlines(para.Inlines, quoteLines[j]);
            }
            return para;
        }

        private static bool IsListItem(string trimmed, out string content, out int? number)
        {
            content = string.Empty;
            number = null;

            var bullet = Regex.Match(trimmed, @"^[-*•]\s+(.*)$");
            if (bullet.Success)
            {
                content = bullet.Groups[1].Value;
                return true;
            }

            var ordered = Regex.Match(trimmed, @"^(\d{1,3})[.)]\s+(.*)$");
            if (ordered.Success)
            {
                content = ordered.Groups[2].Value;
                number = int.Parse(ordered.Groups[1].Value);
                return true;
            }

            return false;
        }

        private static List BuildList(string[] lines, ref int i)
        {
            IsListItem(lines[i].TrimStart(), out var first, out var firstNumber);

            var list = new List
            {
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(22, 0, 0, 0),
                MarkerStyle = firstNumber.HasValue ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc
            };
            if (firstNumber.HasValue)
                list.StartIndex = firstNumber.Value;

            AddListItem(list, first);

            while (i + 1 < lines.Length && IsListItem(lines[i + 1].TrimStart(), out var next, out _))
            {
                i++;
                AddListItem(list, next);
            }

            return list;
        }

        private static void AddListItem(List list, string content)
        {
            var para = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
            AppendInlines(para.Inlines, content);
            list.ListItems.Add(new ListItem(para));
        }

        // --- LaTeX → юникод (T-000136) ---------------------------------------

        // Модель нет-нет да и вставит LaTeX: в тексте видно сырое «$\rightarrow$» вместо стрелки
        // (смок 12.07). Полноценный LaTeX нам не нужен — разворачиваем частые макросы в юникод.
        private static readonly Dictionary<string, string> LatexMacros = new(StringComparer.Ordinal)
        {
            ["rightarrow"] = "→", ["to"] = "→", ["Rightarrow"] = "⇒", ["mapsto"] = "↦",
            ["leftarrow"] = "←", ["gets"] = "←", ["Leftarrow"] = "⇐",
            ["leftrightarrow"] = "↔", ["Leftrightarrow"] = "⇔",
            ["uparrow"] = "↑", ["downarrow"] = "↓",
            ["le"] = "≤", ["leq"] = "≤", ["ge"] = "≥", ["geq"] = "≥",
            ["ne"] = "≠", ["neq"] = "≠", ["approx"] = "≈", ["equiv"] = "≡", ["sim"] = "∼",
            ["times"] = "×", ["div"] = "÷", ["cdot"] = "·", ["pm"] = "±",
            ["infty"] = "∞", ["sum"] = "∑", ["prod"] = "∏", ["sqrt"] = "√", ["partial"] = "∂",
            ["ldots"] = "…", ["dots"] = "…", ["cdots"] = "⋯",
            ["in"] = "∈", ["notin"] = "∉", ["subset"] = "⊂", ["supset"] = "⊃",
            ["cup"] = "∪", ["cap"] = "∩", ["emptyset"] = "∅",
            ["land"] = "∧", ["lor"] = "∨", ["neg"] = "¬", ["forall"] = "∀", ["exists"] = "∃",
            ["alpha"] = "α", ["beta"] = "β", ["gamma"] = "γ", ["delta"] = "δ",
            ["epsilon"] = "ε", ["theta"] = "θ", ["lambda"] = "λ", ["mu"] = "μ",
            ["pi"] = "π", ["rho"] = "ρ", ["sigma"] = "σ", ["tau"] = "τ", ["phi"] = "φ",
            ["omega"] = "ω", ["Delta"] = "Δ", ["Sigma"] = "Σ", ["Omega"] = "Ω",
        };

        private static readonly Regex LatexMacroRe = new(@"\\([A-Za-z]+)", RegexOptions.Compiled);

        // Инлайн-математика в долларах. Ограничение длины — чтобы «$5 … $10» в тексте про деньги
        // не схлопнулось в один «математический» кусок на пол-абзаца.
        private static readonly Regex InlineMathRe = new(@"\$([^$\n]{1,120})\$", RegexOptions.Compiled);

        /// <summary>
        /// Разворачивает частые LaTeX-макросы в юникод. Долларовую обёртку снимаем ТОЛЬКО когда
        /// внутри действительно макрос: «заплатил $5, получил $10» трогать нельзя.
        /// </summary>
        private static string ExpandLatex(string text)
        {
            if (text.IndexOf('\\') < 0) return text;   // подавляющее большинство текстов — быстрый выход

            var s = InlineMathRe.Replace(text, m =>
                m.Groups[1].Value.Contains('\\') ? ExpandMacros(m.Groups[1].Value) : m.Value);

            return ExpandMacros(s);
        }

        /// <summary>Неизвестный макрос оставляем как есть — врать пользователю хуже, чем не знать.</summary>
        private static string ExpandMacros(string s) =>
            LatexMacroRe.Replace(s, m =>
                LatexMacros.TryGetValue(m.Groups[1].Value, out var glyph) ? glyph : m.Value);

        // --- Инлайн ----------------------------------------------------------

        private static readonly Regex InlineToken = new(
            @"(`[^`]+`)|(\*\*[^*]+?\*\*)|(\*[^*\s][^*]*?\*)", RegexOptions.Compiled);

        private static void AppendInlines(InlineCollection inlines, string text)
        {
            int pos = 0;
            foreach (Match m in InlineToken.Matches(text))
            {
                if (m.Index > pos)
                    inlines.Add(new Run(ExpandLatex(text[pos..m.Index])));

                var token = m.Value;
                if (token.StartsWith("`"))
                {
                    // Внутри бэктиков LaTeX НЕ разворачиваем: там код, и «\rightarrow» — это
                    // именно «\rightarrow», а не стрелка.
                    var run = new Run(token[1..^1])
                    {
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 12
                    };
                    run.SetResourceReference(TextElement.BackgroundProperty, "CodeBackground");
                    inlines.Add(run);
                }
                else if (token.StartsWith("**"))
                {
                    inlines.Add(new Bold(new Run(ExpandLatex(token[2..^2]))));
                }
                else
                {
                    inlines.Add(new Italic(new Run(ExpandLatex(token[1..^1]))));
                }

                pos = m.Index + m.Length;
            }

            if (pos < text.Length)
                inlines.Add(new Run(ExpandLatex(text[pos..])));
        }
    }

    /// <summary>
    /// Attached-свойство: биндинг markdown-строки в RichTextBox
    /// (Document не является DP-таргетом для обычного биндинга).
    /// </summary>
    public static class MarkdownBehavior
    {
        public static readonly DependencyProperty MarkdownProperty =
            DependencyProperty.RegisterAttached(
                "Markdown", typeof(string), typeof(MarkdownBehavior),
                new PropertyMetadata(null, OnRenderInputChanged));

        /// <summary>
        /// Команда для кнопки «→ в песочницу» на код-блоках (T-000096). Не задана — кнопок нет.
        /// </summary>
        public static readonly DependencyProperty CodeBlockCommandProperty =
            DependencyProperty.RegisterAttached(
                "CodeBlockCommand", typeof(ICommand), typeof(MarkdownBehavior),
                new PropertyMetadata(null, OnRenderInputChanged));

        public static void SetMarkdown(DependencyObject element, string? value) =>
            element.SetValue(MarkdownProperty, value);

        public static string? GetMarkdown(DependencyObject element) =>
            (string?)element.GetValue(MarkdownProperty);

        public static void SetCodeBlockCommand(DependencyObject element, ICommand? value) =>
            element.SetValue(CodeBlockCommandProperty, value);

        public static ICommand? GetCodeBlockCommand(DependencyObject element) =>
            (ICommand?)element.GetValue(CodeBlockCommandProperty);

        private static void OnRenderInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not RichTextBox rtb) return;
            var cmd = GetCodeBlockCommand(rtb);
            MarkdownRenderer.DiagLog?.Invoke(
                $"render rtb#{rtb.GetHashCode():X} trigger={e.Property.Name} cmd={(cmd == null ? "NULL" : "ok")}");
            rtb.Document = MarkdownRenderer.Render(GetMarkdown(rtb), cmd);
        }
    }
}
