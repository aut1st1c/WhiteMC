#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace WhiteMC.Dev;

internal static class UiKit
{
    // ---------------------------------------------------------------- //
    //  Жёсткая палитра (Mocha). Не тянем из темы — dev-tool должен
    //  выглядеть одинаково вне зависимости от выбранной темы лаунчера.
    // ---------------------------------------------------------------- //

    public static readonly IBrush Bg      = new SolidColorBrush(Color.Parse("#1e1e2e"));
    public static readonly IBrush Mantle  = new SolidColorBrush(Color.Parse("#181825"));
    public static readonly IBrush Card    = new SolidColorBrush(Color.Parse("#313244"));
    public static readonly IBrush Border  = new SolidColorBrush(Color.Parse("#45475a"));
    public static readonly IBrush Text    = new SolidColorBrush(Color.Parse("#cdd6f4"));
    public static readonly IBrush Subtext = new SolidColorBrush(Color.Parse("#a6adc8"));
    public static readonly IBrush Overlay = new SolidColorBrush(Color.Parse("#6c7086"));
    public static readonly IBrush Accent  = new SolidColorBrush(Color.Parse("#89b4fa"));
    public static readonly IBrush Green   = new SolidColorBrush(Color.Parse("#a6e3a1"));
    public static readonly IBrush Yellow  = new SolidColorBrush(Color.Parse("#f9e2af"));
    public static readonly IBrush Red     = new SolidColorBrush(Color.Parse("#f38ba8"));
    public static readonly IBrush RowSep  = new SolidColorBrush(Color.Parse("#2a2a3c"));
    public static readonly IBrush RowHover = new SolidColorBrush(Color.Parse("#3a3a52"));

    // ---------------------------------------------------------------- //
    //  Глобальные стили для окна
    // ---------------------------------------------------------------- //

    /// <summary>
    /// Вешается на DevWindow и все диалоги. Форсит цвета для всех
    /// стандартных контролов, чтобы они не сливались с фоном
    /// вне зависимости от темы лаунчера.
    /// </summary>
    public static void ApplyGlobalStyles(Window window)
    {
        // TextBox
        AddStyle(window, x => x.OfType<TextBox>(),
            (TextBox.BackgroundProperty, Mantle),
            (TextBox.ForegroundProperty, Text),
            (TextBox.BorderBrushProperty, Border),
            (TextBox.CaretBrushProperty, Accent),
            (TextBox.SelectionBrushProperty, Accent),
            (TextBox.SelectionForegroundBrushProperty, Mantle),
            (TextBox.PaddingProperty, new Thickness(8, 6)));

        // ComboBox
        AddStyle(window, x => x.OfType<ComboBox>(),
            (ComboBox.BackgroundProperty, Mantle),
            (ComboBox.ForegroundProperty, Text),
            (ComboBox.BorderBrushProperty, Border),
            (ComboBox.PaddingProperty, new Thickness(8, 6)));

        // Button — обычные
        AddStyle(window, x => x.OfType<Button>(),
            (Button.BackgroundProperty, Card),
            (Button.ForegroundProperty, Text),
            (Button.BorderBrushProperty, Border));

        // CheckBox — только текст
        AddStyle(window, x => x.OfType<CheckBox>(),
            (CheckBox.ForegroundProperty, Text));

        // RadioButton
        AddStyle(window, x => x.OfType<RadioButton>(),
            (RadioButton.ForegroundProperty, Text));

        // TextBlock по умолчанию
        AddStyle(window, x => x.OfType<TextBlock>(),
            (TextBlock.ForegroundProperty, Text));

        // ListBox
        AddStyle(window, x => x.OfType<ListBox>(),
            (ListBox.BackgroundProperty, Bg),
            (ListBox.ForegroundProperty, Text),
            (ListBox.BorderBrushProperty, Border));

        // ListBoxItem — базовый
        AddStyle(window, x => x.OfType<ListBoxItem>(),
            (ListBoxItem.BackgroundProperty, Brushes.Transparent),
            (ListBoxItem.ForegroundProperty, Text),
            (ListBoxItem.PaddingProperty, new Thickness(0)),
            (ListBoxItem.MarginProperty, new Thickness(0)),
            (ListBoxItem.MinHeightProperty, 26.0));

        // ListBoxItem — hover
        AddStyle(window, x => x.OfType<ListBoxItem>().Class(":pointerover"),
            (ListBoxItem.BackgroundProperty, RowHover));

        // ListBoxItem — selected
        AddStyle(window, x => x.OfType<ListBoxItem>().Class(":selected"),
            (ListBoxItem.BackgroundProperty, Accent),
            (ListBoxItem.ForegroundProperty, Mantle));

        // TabItem
        AddStyle(window, x => x.OfType<TabItem>(),
            (TabItem.ForegroundProperty, Subtext));
    }

    private static void AddStyle(Window w, Func<Selector, Selector> selector,
                                 params (AvaloniaProperty prop, object value)[] setters)
    {
        var style = new Style(selector);
        foreach (var (prop, value) in setters)
            style.Setters.Add(new Setter(prop, value));
        w.Styles.Add(style);
    }

    // ---------------------------------------------------------------- //
    //  Заголовки и подписи
    // ---------------------------------------------------------------- //

    public static TextBlock H1(string text) => new()
    {
        Text = text,
        FontSize = 20,
        FontWeight = FontWeight.SemiBold,
        Foreground = Text
    };

    public static TextBlock H2(string text) => new()
    {
        Text = text,
        FontSize = 14,
        FontWeight = FontWeight.SemiBold,
        Foreground = Text,
        Margin = new Thickness(0, 8, 0, 4)
    };

    public static TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = Subtext,
        Margin = new Thickness(0, 0, 0, 2)
    };

    public static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 10,
        Foreground = Overlay,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 0)
    };

    // ---------------------------------------------------------------- //
    //  Поля форм
    // ---------------------------------------------------------------- //

    public static Control Field(string label, Control input, string hint = null)
    {
        var stack = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 4) };
        stack.Children.Add(Label(label));
        stack.Children.Add(input);
        if (!string.IsNullOrEmpty(hint)) stack.Children.Add(Hint(hint));
        return stack;
    }

    public static (Control field, TextBox box, Button browse) BrowsableField(
        string label, string value, Func<TextBox, Task> onBrowse)
    {
        var box = new TextBox { Text = value };
        var btn = new Button
        {
            Content = "…",
            Width = 36,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0)
        };
        btn.Click += async (_, _) => await onBrowse(box);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(box, 0);
        Grid.SetColumn(btn, 1);
        grid.Children.Add(box);
        grid.Children.Add(btn);

        return (Field(label, grid), box, btn);
    }

    // ---------------------------------------------------------------- //
    //  Карточки
    // ---------------------------------------------------------------- //

    public static Border CardBox(string title = null)
    {
        var b = new Border
        {
            Background = Card,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 6, 0, 0)
        };
        var stack = new StackPanel { Spacing = 6 };
        if (!string.IsNullOrEmpty(title))
            stack.Children.Add(H2(title));
        b.Child = stack;
        return b;
    }

    public static void AddToCard(Border card, params Control[] children)
    {
        StackPanel stack;
        if (card.Child is StackPanel existing)
        {
            stack = existing;
        }
        else
        {
            stack = new StackPanel { Spacing = 6 };
            if (card.Child is Control c0) stack.Children.Add(c0);
        }
        card.Child = stack;
        foreach (var c in children) stack.Children.Add(c);
    }

    // ---------------------------------------------------------------- //
    //  Таблица
    // ---------------------------------------------------------------- //

    public sealed class TableBuilder
    {
        public Border Root { get; }
        public ListBox List { get; }

        private readonly double[] _widths;
        private int _rowIndex;

        public TableBuilder(params (string label, double width)[] cols)
        {
            _widths = cols.Select(c => c.width).ToArray();

            // ---------- header ----------
            var header = new Grid
            {
                ColumnDefinitions = MakeCols(_widths),
                Height = 32,
                Margin = new Thickness(10, 0, 10, 0)
            };
            for (int i = 0; i < cols.Length; i++)
            {
                var tb = new TextBlock
                {
                    Text = cols[i].label,
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = Subtext,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 8, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(tb, i);
                header.Children.Add(tb);
            }

            // ---------- list ----------
            List = new ListBox
            {
                Padding = new Thickness(0),
                BorderThickness = new Thickness(0),
                Background = Bg,
                SelectionMode = SelectionMode.Single
            };

            var dock = new DockPanel { LastChildFill = true };

            var top = new Border
            {
                Background = Mantle,
                BorderBrush = Border,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 4),
                Child = header
            };
            DockPanel.SetDock(top, Dock.Top);
            dock.Children.Add(top);
            dock.Children.Add(List);

            Root = new Border
            {
                Background = Bg,
                BorderBrush = Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                ClipToBounds = true,
                MinHeight = 220,
                Child = dock
            };
        }

        public ListBoxItem AddRow(string tag, params string[] cells)
        {
            var grid = new Grid
            {
                ColumnDefinitions = MakeCols(_widths),
                Height = 26
            };
            for (int i = 0; i < _widths.Length; i++)
            {
                var text = i < cells.Length ? cells[i] : "";
                var tb = new TextBlock
                {
                    Text = text,
                    FontSize = 12,
                    Foreground = Text,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 8, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                Grid.SetColumn(tb, i);
                grid.Children.Add(tb);
            }

            // Разделитель под каждой строкой — иначе на тёмном фоне
            // строки сливаются в сплошной блок.
            var rowBorder = new Border
            {
                BorderBrush = RowSep,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 2, 0, 2),
                Child = grid
            };

            var item = new ListBoxItem
            {
                Content = rowBorder,
                Tag = tag,
                Padding = new Thickness(0),
                Margin = new Thickness(0),
                MinHeight = 30
            };
            List.Items.Add(item);
            _rowIndex++;
            return item;
        }

        public void Clear()
        {
            List.Items.Clear();
            _rowIndex = 0;
        }

        public string SelectedTag =>
            (List.SelectedItem as ListBoxItem)?.Tag as string;

        public List<string> SelectedTags
        {
            get
            {
                var result = new List<string>();
                foreach (var item in List.SelectedItems)
                    if (item is ListBoxItem l && l.Tag is string s) result.Add(s);
                return result;
            }
        }

        private static ColumnDefinitions MakeCols(double[] widths)
        {
            var defs = new ColumnDefinitions();
            foreach (var w in widths)
                defs.Add(new ColumnDefinition(w, GridUnitType.Pixel));
            return defs;
        }
    }
}

#nullable restore