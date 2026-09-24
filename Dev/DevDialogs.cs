using Avalonia.Input;
#nullable disable
using Avalonia.Interactivity;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using WhiteMC.Core;
using Avalonia.Controls.Primitives;

namespace WhiteMC.Dev;

// ==================================================================== //
//  Базовый диалог
// ==================================================================== //

public abstract class DevDialogBase : Window
{
    protected readonly StackPanel Body;

    protected DevDialogBase(string title, double width = 600)
    {
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 800;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = UiKit.Bg;
        Foreground = UiKit.Text;

        // Убираем системный крестик: закрытие через него раньше молча
        // отбрасывало изменения. Теперь закрыть можно только через
        // кнопки OK / Отмена или клавиши Enter / Escape.
        SystemDecorations = SystemDecorations.None;

        UiKit.ApplyGlobalStyles(this);

        var root = new DockPanel { LastChildFill = true };

        // Кнопки снизу
        var buttons = new Border
        {
            Background = UiKit.Mantle,
            Padding = new Thickness(14, 10),
            BorderBrush = UiKit.Border,
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        buttons.Child = BuildButtons();
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        Body = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(16, 14, 16, 14)
        };
        root.Children.Add(new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = Body
        });

        Content = root;

        // Enter = OK, Escape = Отмена. Ловим на уровне окна, чтобы работало
        // вне зависимости от того, какой контрол в фокусе.
        AddHandler(KeyDownEvent, OnDialogKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnDialogKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnOk();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close(null);
            e.Handled = true;
        }
    }

    private Control BuildButtons()
    {
        var sp = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var cancel = new Button { Content = "Отмена", Padding = new Thickness(14, 6) };
        cancel.Click += (_, _) => Close(null);
        var ok = new Button
        {
            Content = "OK",
            Padding = new Thickness(14, 6),
            Background = UiKit.Accent,
            Foreground = UiKit.Mantle,
            FontWeight = FontWeight.SemiBold
        };
        ok.Click += (_, _) => OnOk();
        sp.Children.Add(cancel);
        sp.Children.Add(ok);
        return sp;
    }

    protected abstract void OnOk();
}


// ==================================================================== //
//  Диалог профиля
// ==================================================================== //

public class DevProfileDialog : DevDialogBase
{
    private readonly DevProfile _p;
    private readonly bool _isNew;
    private readonly string _baseUrl;
    private readonly string _modsBaseDir;

    private TextBox _keyBox = null!;
    private TextBox _nameBox = null!;
    private TextBox _descBox = null!;
    private TextBox _mcBox = null!;
    private TextBox _modsDirBox = null!;
    private TextBox _manifestBox = null!;
    private TextBox _optionalBox = null!;
    private TextBox _archiveBox = null!;
    private CheckBox _nfBox = null!;
    private CheckBox _nfOptBox = null!;
    private CheckBox _optModsBox = null!;
    private TextBlock _manifestHint = null!;
    private TextBlock _optionalHint = null!;
    private TextBlock _archiveHint = null!;

    public DevProfileDialog(DevProfile p, bool isNew, string baseUrl, string modsBaseDir)
        : base(isNew ? "Новый профиль" : $"Профиль — {p.Key}", 660)
    {
        _p = p;
        _isNew = isNew;
        _baseUrl = baseUrl;
        _modsBaseDir = modsBaseDir;

        if (_isNew && string.IsNullOrEmpty(p.ModsDir) && !string.IsNullOrEmpty(modsBaseDir))
            p.ModsDir = DevStateService.DefaultModsDirFor(modsBaseDir, p.Key);

        // ---- Card: основное ----
        var card1 = new Border
        {
            Background = UiKit.Card,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 4, 0, 4)
        };
        var s1 = new StackPanel { Spacing = 6 };
        card1.Child = s1;

        _keyBox = new TextBox { Text = p.Key };
        if (!_isNew) _keyBox.IsReadOnly = true;
        s1.Children.Add(UiKit.Field("Ключ (ID профиля)", _keyBox));

        _nameBox = new TextBox { Text = p.DisplayName };
        s1.Children.Add(UiKit.Field("Отображаемое имя", _nameBox));

        _descBox = new TextBox { Text = p.Description };
        s1.Children.Add(UiKit.Field("Описание", _descBox));

        _mcBox = new TextBox
        {
            Text = p.Mc,
            Width = 120,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        s1.Children.Add(UiKit.Field("Minecraft", _mcBox));

        var flags = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            Margin = new Thickness(0, 6, 0, 0)
        };
        _nfBox = new CheckBox { Content = "NeoForge обязателен", IsChecked = p.NeoForge };
        _nfOptBox = new CheckBox { Content = "NeoForge опционален", IsChecked = p.NeoForgeOptional };
        _optModsBox = new CheckBox { Content = "Разрешить опц. моды", IsChecked = p.OptionalMods };
        flags.Children.Add(_nfBox);
        flags.Children.Add(_nfOptBox);
        flags.Children.Add(_optModsBox);
        s1.Children.Add(UiKit.Label("NeoForge / опциональные моды"));
        s1.Children.Add(flags);

        Body.Children.Add(card1);

        // ---- Card: папка модов ----
        var card2 = new Border
        {
            Background = UiKit.Card,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 4, 0, 4)
        };
        var s2 = new StackPanel { Spacing = 6 };
        card2.Child = s2;

        var dirGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        _modsDirBox = new TextBox { Text = p.ModsDir };
        var dirBtn = new Button { Content = "…", Width = 36, Margin = new Thickness(4, 0, 0, 0) };
        dirBtn.Click += async (_, _) => await PickModsDir();
        Grid.SetColumn(_modsDirBox, 0);
        Grid.SetColumn(dirBtn, 1);
        dirGrid.Children.Add(_modsDirBox);
        dirGrid.Children.Add(dirBtn);
        s2.Children.Add(UiKit.Field("Папка с модами",
            dirGrid,
            "Своя папка для каждого профиля — например <mods_base_dir>/<Key>."));
        Body.Children.Add(card2);

        // ---- Card: ссылки ----
        var card3 = new Border
        {
            Background = UiKit.Card,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 4, 0, 4)
        };
        var s3 = new StackPanel { Spacing = 6 };
        card3.Child = s3;
        s3.Children.Add(UiKit.H2("Ссылки на манифесты"));
        s3.Children.Add(UiKit.Hint("Имя файла (без .json/.zip) ИЛИ полный URL. Пусто = авто-имя от ключа."));

        _manifestBox = new TextBox { Text = p.ManifestUrlOverride };
        s3.Children.Add(UiKit.Field("manifest_url", _manifestBox));
        _manifestHint = UiKit.Hint("");
        s3.Children.Add(_manifestHint);

        _optionalBox = new TextBox { Text = p.OptionalManifestUrlOverride };
        s3.Children.Add(UiKit.Field("optional_manifest_url", _optionalBox));
        _optionalHint = UiKit.Hint("");
        s3.Children.Add(_optionalHint);

        _archiveBox = new TextBox { Text = p.ModsArchiveUrlOverride };
        s3.Children.Add(UiKit.Field("mods_archive_url", _archiveBox));
        _archiveHint = UiKit.Hint("");
        s3.Children.Add(_archiveHint);

        _manifestBox.TextChanged += (_, _) => UpdatePreviews();
        _optionalBox.TextChanged += (_, _) => UpdatePreviews();
        _archiveBox.TextChanged += (_, _) => UpdatePreviews();
        _keyBox.TextChanged += (_, _) => UpdatePreviews();

        Body.Children.Add(card3);
        UpdatePreviews();
    }

    private void UpdatePreviews()
    {
        var key = string.IsNullOrEmpty(_keyBox.Text) ? "Profile" : _keyBox.Text!;
        var slug = DevStateService.Slug(key);
        _manifestHint.Text = "→ " + DevStateService.ResolveUrl(_baseUrl, _manifestBox.Text ?? "",
            $"manifest-{slug}", ".json");
        _optionalHint.Text = "→ " + DevStateService.ResolveUrl(_baseUrl, _optionalBox.Text ?? "",
            $"optional-{slug}", ".json");
        _archiveHint.Text = "→ " + DevStateService.ResolveUrl(_baseUrl, _archiveBox.Text ?? "",
            $"mods-{slug}", ".zip");
    }

    private async Task PickModsDir()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions());
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            _modsDirBox.Text = path;
    }

    protected override void OnOk()
    {
        var key = (_keyBox.Text ?? "").Trim();
        if (string.IsNullOrEmpty(key))
        {
            _ = Dialogs.WarnAsync(this, "Dev", "Укажите ключ.");
            return;
        }
        foreach (var c in key)
        {
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '-')
            {
                _ = Dialogs.WarnAsync(this, "Dev", "Ключ: только буквы, цифры, '_' и '-'.");
                return;
            }
        }

        _p.Key = key;
        _p.DisplayName = (_nameBox.Text ?? "").Trim();
        _p.Description = (_descBox.Text ?? "").Trim();
        _p.Mc = string.IsNullOrWhiteSpace(_mcBox.Text) ? "1.21.1" : _mcBox.Text!.Trim();
        _p.NeoForge = _nfBox.IsChecked == true;
        _p.NeoForgeOptional = _nfOptBox.IsChecked == true;
        _p.OptionalMods = _optModsBox.IsChecked == true;

        var md = (_modsDirBox.Text ?? "").Trim();
        if (string.IsNullOrEmpty(md) && !string.IsNullOrEmpty(_modsBaseDir))
            md = DevStateService.DefaultModsDirFor(_modsBaseDir, key);
        _p.ModsDir = md;

        _p.ManifestUrlOverride = (_manifestBox.Text ?? "").Trim();
        _p.OptionalManifestUrlOverride = (_optionalBox.Text ?? "").Trim();
        _p.ModsArchiveUrlOverride = (_archiveBox.Text ?? "").Trim();

        Close(_p);
    }
}


// ==================================================================== //
//  Диалог группы
// ==================================================================== //

public class DevGroupDialog : DevDialogBase
{
    private readonly DevGroup _g;
    private readonly List<DevGroup> _others;

    private TextBox _idBox = null!;
    private TextBox _nameBox = null!;
    private TextBox _descBox = null!;
    private TextBox _exclBox = null!;
    private ComboBox _modeBox = null!;
    private ListBox _depsList = null!;
    private CheckBox _defaultBox = null!;

    // Параметр без '?' — контекст уже #nullable disable.
    public DevGroupDialog(DevGroup gObj, List<DevGroup> others)
        : base(gObj == null ? "Новая группа" : $"Группа — {gObj.Id}", 580)
    {
        _g = gObj ?? new DevGroup();
        _others = others;

        var card1 = new Border
        {
            Background = UiKit.Card,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 4, 0, 4)
        };
        var s1 = new StackPanel { Spacing = 6 };
        card1.Child = s1;

        _idBox = new TextBox { Text = _g.Id };
        s1.Children.Add(UiKit.Field("ID группы", _idBox,
            "Латиница, цифры, '_' и '-'. По нему моды ссылаются на группу."));

        _nameBox = new TextBox { Text = _g.DisplayName };
        s1.Children.Add(UiKit.Field("Отображаемое имя", _nameBox));

        _descBox = new TextBox { Text = _g.Description };
        s1.Children.Add(UiKit.Field("Описание", _descBox));

        _modeBox = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                "grouped — визуальная группа, моды независимы",
                "bundle — один чекбокс на всю группу",
                "exclusive — радио внутри группы"
            }
        };
        _modeBox.SelectedIndex = _g.Mode switch
        {
            "bundle" => 1,
            "exclusive" => 2,
            _ => 0
        };
        s1.Children.Add(UiKit.Field("Режим", _modeBox));

        _exclBox = new TextBox { Text = _g.ExclusiveSet ?? "" };
        s1.Children.Add(UiKit.Field("Exclusive-set", _exclBox,
            "Группы с одинаковым значением взаимоисключающие (например recipe_viewer)."));

        Body.Children.Add(card1);

        // ---- Card: зависимости ----
        var card2 = new Border
        {
            Background = UiKit.Card,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 4, 0, 4)
        };
        var s2 = new StackPanel { Spacing = 6 };
        card2.Child = s2;
        s2.Children.Add(UiKit.H2("Зависит от групп"));
        s2.Children.Add(UiKit.Hint("Если включить мод из этой группы — эти группы включатся первыми."));

        _depsList = new ListBox
        {
            SelectionMode = SelectionMode.Multiple,
            Height = 140
        };
        foreach (var depRaw in _others)
        {
            var dep = depRaw!;
            _depsList.Items.Add(new ListBoxItem
            {
                Content = $"{dep.DisplayName}  [{dep.Id}]",
                Tag = dep.Id,
                IsSelected = _g.DependsOn.Contains(dep.Id)
            });
        }
        s2.Children.Add(_depsList);

        _defaultBox = new CheckBox
        {
            Content = "Включена по умолчанию",
            IsChecked = _g.EnabledOnDefault,
            Margin = new Thickness(0, 6, 0, 0)
        };
        s2.Children.Add(_defaultBox);

        Body.Children.Add(card2);
    }

    protected override void OnOk()
    {
        var id = (_idBox.Text ?? "").Trim();
        if (string.IsNullOrEmpty(id))
        {
            _ = Dialogs.WarnAsync(this, "Dev", "Укажите ID.");
            return;
        }
        foreach (var c in id)
        {
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '-')
            {
                _ = Dialogs.WarnAsync(this, "Dev", "ID: только буквы, цифры, '_' и '-'.");
                return;
            }
        }

        _g.Id = id;
        _g.DisplayName = (_nameBox.Text ?? "").Trim();
        _g.Description = (_descBox.Text ?? "").Trim();
        _g.Mode = _modeBox.SelectedIndex switch
        {
            1 => "bundle",
            2 => "exclusive",
            _ => "grouped"
        };
        _g.ExclusiveSet = string.IsNullOrWhiteSpace(_exclBox.Text) ? null : _exclBox.Text!.Trim();

        _g.DependsOn.Clear();
        foreach (var item in _depsList.SelectedItems)
            if (item is ListBoxItem lbi && lbi.Tag is string gid)
                _g.DependsOn.Add(gid);

        _g.EnabledOnDefault = _defaultBox.IsChecked == true;
        Close(_g);
    }
}


// ==================================================================== //
//  Диалог мода
// ==================================================================== //

public class DevModDialog : DevDialogBase
{
    private readonly DevMod _m;
    private readonly List<DevGroup> _groups;
    private readonly List<DevMod> _otherMods;

    private CheckBox _optBox = null!;
    private TextBox _nameBox = null!;
    private TextBox _descBox = null!;
    private ListBox _groupsList = null!;
    private ListBox _depsList = null!;
    private ComboBox _defaultBox = null!;

    public DevModDialog(DevMod m, List<DevGroup> groups, List<DevMod> otherMods)
        : base($"Мод — {m.Filename}", 660)
    {
        _m = m;
        _groups = groups;
        _otherMods = otherMods;

        // ---- Card: инфо ----
        var info = new Border
        {
            Background = UiKit.Mantle,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8),
            Margin = new Thickness(0, 4, 0, 4)
        };
        info.Child = new TextBlock
        {
            Text = $"Файл: {m.Filename}\nРазмер: {m.Size / 1024.0:F1} КБ\nИсточник: {m.Source}",
            FontFamily = new FontFamily("Consolas, monospace"),
            FontSize = 12,
            Foreground = UiKit.Subtext
        };
        Body.Children.Add(info);

        // ---- Card: основное ----
        var card1 = new Border
        {
            Background = UiKit.Card,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 4, 0, 4)
        };
        var s1 = new StackPanel { Spacing = 6 };
        card1.Child = s1;

        _optBox = new CheckBox
        {
            Content = "Опциональный (иначе — обязательный)",
            IsChecked = m.IsOptional,
            Margin = new Thickness(0, 4, 0, 4)
        };
        s1.Children.Add(_optBox);

        _nameBox = new TextBox { Text = m.DisplayName };
        s1.Children.Add(UiKit.Field("Отображаемое имя", _nameBox));

        _descBox = new TextBox { Text = m.Description };
        s1.Children.Add(UiKit.Field("Описание", _descBox));

        var cur = m.EnabledOnDefault switch
        {
            true => 1,
            false => 2,
            _ => 0
        };
        _defaultBox = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Items =
            {
                "Наследовать от группы",
                "По умолчанию включён",
                "По умолчанию выключен"
            },
            SelectedIndex = cur
        };
        s1.Children.Add(UiKit.Field("По умолчанию", _defaultBox));

        Body.Children.Add(card1);

        // ---- Card: группы ----
        var card2 = new Border
        {
            Background = UiKit.Card,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 4, 0, 4)
        };
        var s2 = new StackPanel { Spacing = 6 };
        card2.Child = s2;
        s2.Children.Add(UiKit.H2("Группы"));
        s2.Children.Add(UiKit.Hint("Ctrl+клик — несколько. Мод попадёт в первую из списка."));

        _groupsList = new ListBox
        {
            SelectionMode = SelectionMode.Multiple,
            Height = 120
        };
        foreach (var grpRaw in _groups)
        {
            var grp = grpRaw!;
            _groupsList.Items.Add(new ListBoxItem
            {
                Content = $"{grp.DisplayName}  [{grp.Id}]",
                Tag = grp.Id,
                IsSelected = m.Groups.Contains(grp.Id)
            });
        }
        s2.Children.Add(_groupsList);
        Body.Children.Add(card2);

        // ---- Card: зависимости ----
        var card3 = new Border
        {
            Background = UiKit.Card,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 4, 0, 4)
        };
        var s3 = new StackPanel { Spacing = 6 };
        card3.Child = s3;
        s3.Children.Add(UiKit.H2("Зависит от"));
        s3.Children.Add(UiKit.Hint("Группы и отдельные моды. Ctrl+клик — несколько."));

        _depsList = new ListBox
        {
            SelectionMode = SelectionMode.Multiple,
            Height = 160
        };
        foreach (var grpRaw in _groups)
        {
            var grp = grpRaw!;
            _depsList.Items.Add(new ListBoxItem
            {
                Content = $"[GROUP]  {grp.DisplayName}  [{grp.Id}]",
                Tag = grp.Id,
                IsSelected = m.DependsOn.Contains(grp.Id)
            });
        }
        foreach (var omRaw in _otherMods)
        {
            var om = omRaw!;
            _depsList.Items.Add(new ListBoxItem
            {
                Content = $"[MOD]    {om.DisplayName}  [{om.Filename}]",
                Tag = om.Filename,
                IsSelected = m.DependsOn.Contains(om.Filename)
            });
        }
        s3.Children.Add(_depsList);
        Body.Children.Add(card3);
    }

    protected override void OnOk()
    {
        _m.IsOptional = _optBox.IsChecked == true;
        _m.DisplayName = (_nameBox.Text ?? "").Trim();
        _m.Description = (_descBox.Text ?? "").Trim();

        _m.Groups.Clear();
        foreach (var item in _groupsList.SelectedItems)
            if (item is ListBoxItem l && l.Tag is string gid)
                _m.Groups.Add(gid);

        _m.DependsOn.Clear();
        foreach (var item in _depsList.SelectedItems)
            if (item is ListBoxItem l && l.Tag is string dep)
                _m.DependsOn.Add(dep);

        _m.EnabledOnDefault = _defaultBox.SelectedIndex switch
        {
            1 => true,
            2 => false,
            _ => (bool?)null
        };

        Close(_m);
    }
}
#nullable restore