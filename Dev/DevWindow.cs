using Avalonia.Controls.Primitives;
#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using WhiteMC.Core;

namespace WhiteMC.Dev;

public class DevWindow : Window
{
    private readonly DevState _state;
    private bool _busy;

    private ComboBox _activeCombo;

    // Settings
    private TextBox _baseUrl;
    private TextBox _launcherVer;
    private TextBox _launcherDl;
    private TextBox _manifestVer;
    private TextBox _outputDir;
    private TextBox _modsBaseDir;

    // Tables
    private UiKit.TableBuilder _profilesTable;
    private UiKit.TableBuilder _modsTable;
    private UiKit.TableBuilder _groupsTable;

    // Log
    private TextBox _log;

    public DevWindow()
    {
        Title = "WhiteMC — dev editor";
        Width = 1320;
        Height = 860;
        MinWidth = 1000;
        MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = UiKit.Bg;
        Foreground = UiKit.Text;

        _state = DevStateService.Load();

        UiKit.ApplyGlobalStyles(this);
        BuildUi();
        RefreshAll();
    }

    // ---------------------------------------------------------------- //
    //  Layout
    // ---------------------------------------------------------------- //

    private void BuildUi()
    {
        var dock = new DockPanel { LastChildFill = true };

        var top = new Border
        {
            Background = UiKit.Mantle,
            Padding = new Thickness(14, 10),
            BorderBrush = UiKit.Border,
            BorderThickness = new Thickness(0, 0, 0, 1)
        };
        top.Child = BuildToolbar();
        DockPanel.SetDock(top, Dock.Top);
        dock.Children.Add(top);

        var bottom = new Border
        {
            Background = UiKit.Mantle,
            Padding = new Thickness(14, 6),
            BorderBrush = UiKit.Border,
            BorderThickness = new Thickness(0, 1, 0, 0)
        };
        bottom.Child = new TextBlock
        {
            Text = "Dev mode · F12 или Ctrl+Shift+Alt+D для закрытия",
            FontSize = 11,
            Foreground = UiKit.Overlay
        };
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(bottom);

        var tabs = new TabControl { Margin = new Thickness(14, 10, 14, 10) };
        tabs.Items.Add(new TabItem { Header = "Настройки", Content = WrapPage(BuildSettingsTab()) });
        tabs.Items.Add(new TabItem { Header = "Профили",   Content = WrapPage(BuildProfilesTab()) });
        tabs.Items.Add(new TabItem { Header = "Моды",      Content = WrapPage(BuildModsTab()) });
        tabs.Items.Add(new TabItem { Header = "Группы",    Content = WrapPage(BuildGroupsTab()) });
        tabs.Items.Add(new TabItem { Header = "Флаги",     Content = WrapPage(BuildFlagsTab()) });
        tabs.Items.Add(new TabItem { Header = "Лог",       Content = WrapPage(BuildLogTab()) });
        dock.Children.Add(tabs);

        Content = dock;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F12 ||
                (e.Key == Key.D && e.KeyModifiers.HasFlag(KeyModifiers.Control)
                                 && e.KeyModifiers.HasFlag(KeyModifiers.Shift)
                                 && e.KeyModifiers.HasFlag(KeyModifiers.Alt)))
            {
                SaveState();
                Close();
                e.Handled = true;
            }
        };
    }

    private static Control WrapPage(Control inner)
    {
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = inner
        };
    }

    private Control BuildToolbar()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto")
        };

        var lbl = new TextBlock
        {
            Text = "Профиль:",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = UiKit.Subtext,
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(lbl, 0);
        grid.Children.Add(lbl);

        _activeCombo = new ComboBox { Width = 240, VerticalAlignment = VerticalAlignment.Center };
        _activeCombo.SelectionChanged += (_, _) => OnActiveChanged();
        Grid.SetColumn(_activeCombo, 1);
        grid.Children.Add(_activeCombo);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        right.Children.Add(MakeButton("🔍  Сканировать", async () => await ScanActiveAsync()));
        right.Children.Add(MakeButton("💾  Сгенерировать",
            () => { GenerateAll(); return Task.CompletedTask; }, primary: true));
        right.Children.Add(MakeButton("Сохранить",
            () => { SaveState(); Log("Состояние сохранено."); return Task.CompletedTask; }));
        Grid.SetColumn(right, 3);
        grid.Children.Add(right);

        return grid;
    }

    private static Button MakeButton(string text, Func<Task> handler, bool primary = false)
    {
        var b = new Button
        {
            Content = text,
            Padding = new Thickness(12, 6),
            CornerRadius = new CornerRadius(4)
        };
        if (primary)
        {
            b.Background = UiKit.Accent;
            b.Foreground = UiKit.Mantle;
            b.FontWeight = FontWeight.SemiBold;
        }
        b.Click += async (_, _) => await handler();
        return b;
    }

    // ---------------------------------------------------------------- //
    //  Settings
    // ---------------------------------------------------------------- //

    private Control BuildSettingsTab()
    {
        var s = _state.Settings;
        var root = new StackPanel { Spacing = 6, Margin = new Thickness(0, 6, 0, 6) };

        var card1 = UiKit.CardBox("links.json");

        _baseUrl = new TextBox { Text = s.BaseUrl };
        _launcherVer = new TextBox { Text = s.LauncherVersion };
        _launcherDl = new TextBox { Text = s.LauncherDownloadUrl };

        UiKit.AddToCard(card1,
            UiKit.Field("base_url", _baseUrl,
                "Корень, куда клеятся override-имена из профилей."),
            UiKit.Field("launcher_version", _launcherVer),
            UiKit.Field("launcher_download_url", _launcherDl));
        root.Children.Add(card1);

        var card2 = UiKit.CardBox("Генерация");

        _manifestVer = new TextBox { Text = s.ManifestVersion };

        var outputTuple = UiKit.BrowsableField(
            "output_dir (пусто = рядом с .exe)",
            s.OutputDir,
            PickFolderInto);
        _outputDir = outputTuple.box;

        var modsBaseTuple = UiKit.BrowsableField(
            "mods_base_dir (корень для папок профилей)",
            s.ModsBaseDir,
            PickFolderInto);
        _modsBaseDir = modsBaseTuple.box;

        UiKit.AddToCard(card2,
            UiKit.Field("manifest_version", _manifestVer,
                "Если пусто — применится timestamp %Y.%m.%d-%H%M."),
            outputTuple.field,
            modsBaseTuple.field);

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 8, 0, 0)
        };

        var applyBtn = new Button { Content = "Применить", Padding = new Thickness(12, 6) };
        applyBtn.Click += (_, _) =>
        {
            ApplySettings();
            SaveState();
            Log("Настройки применены.");
        };
        btns.Children.Add(applyBtn);

        var applyAllBtn = new Button
        {
            Content = "mods_dir → всем пустым профилям",
            Padding = new Thickness(12, 6)
        };
        applyAllBtn.Click += (_, _) => ApplyModsBaseToAll();
        btns.Children.Add(applyAllBtn);

        UiKit.AddToCard(card2, btns);
        root.Children.Add(card2);

        return root;
    }

    private async Task PickFolderInto(TextBox target)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions());
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
            target.Text = path;
    }

    private void ApplySettings()
    {
        var s = _state.Settings;
        s.BaseUrl = (_baseUrl.Text ?? "").TrimEnd('/');
        s.LauncherVersion = _launcherVer.Text ?? "";
        s.LauncherDownloadUrl = _launcherDl.Text ?? "";
        s.ManifestVersion = _manifestVer.Text ?? "";
        s.OutputDir = _outputDir.Text ?? "";
        s.ModsBaseDir = _modsBaseDir.Text ?? "";
    }

    private void ApplyModsBaseToAll()
    {
        ApplySettings();
        var baseDir = _state.Settings.ModsBaseDir;
        if (string.IsNullOrEmpty(baseDir))
        {
            _ = Dialogs.WarnAsync(this, "Dev", "mods_base_dir не задан.");
            return;
        }
        int n = 0;
        foreach (var kv in _state.Profiles)
        {
            var p = kv.Value;
            if (string.IsNullOrEmpty(p.ModsDir))
            {
                p.ModsDir = DevStateService.DefaultModsDirFor(baseDir, kv.Key);
                n++;
            }
        }
        RefreshProfiles();
        SaveState();
        Log($"mods_dir проставлен для {n} профилей.");
    }

    // ---------------------------------------------------------------- //
    //  Profiles
    // ---------------------------------------------------------------- //

    private Control BuildProfilesTab()
    {
        _profilesTable = new UiKit.TableBuilder(
            ("Ключ",        160),
            ("Имя",         180),
            ("MC",           70),
            ("NeoForge",     90),
            ("Опц.моды",     90),
            ("Обяз.",        60),
            ("Опц.",         60),
            ("Групп",        60),
            ("Папка модов", 320));
        _profilesTable.List.SelectionMode = SelectionMode.Single;
        _profilesTable.List.DoubleTapped += (_, _) => _ = EditProfileAsync();

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 10, 0, 0)
        };
        btns.Children.Add(MakeButton("Добавить…",
            () => { _ = AddProfileAsync(); return Task.CompletedTask; }, primary: true));
        btns.Children.Add(MakeButton("Изменить…",
            () => { _ = EditProfileAsync(); return Task.CompletedTask; }));
        btns.Children.Add(MakeButton("Удалить",
            () => { RemoveProfile(); return Task.CompletedTask; }));
        btns.Children.Add(MakeButton("↑",
            () => { MoveProfile(-1); return Task.CompletedTask; }));
        btns.Children.Add(MakeButton("↓",
            () => { MoveProfile(+1); return Task.CompletedTask; }));

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(btns, Dock.Bottom);
        dock.Children.Add(btns);
        dock.Children.Add(_profilesTable.Root);
        return dock;
    }

    private void RefreshProfiles()
    {
        _profilesTable.Clear();
        foreach (var kv in _state.Profiles)
        {
            var key = kv.Key;
            var p = kv.Value;
            int req = p.Mods.Count(m => !m.IsOptional);
            int opt = p.Mods.Count(m => m.IsOptional);
            string nf = p.NeoForge ? "да" : (p.NeoForgeOptional ? "опц." : "—");
            _profilesTable.AddRow(key,
                key,
                string.IsNullOrEmpty(p.DisplayName) ? "—" : p.DisplayName,
                p.Mc,
                nf,
                p.OptionalMods ? "да" : "нет",
                req.ToString(),
                opt.ToString(),
                p.Groups.Count.ToString(),
                string.IsNullOrEmpty(p.ModsDir) ? "—" : p.ModsDir);
        }

        var cur = _activeCombo.SelectedItem as string;
        _activeCombo.Items.Clear();
        foreach (var k in _state.Profiles.Keys) _activeCombo.Items.Add(k);
        if (!string.IsNullOrEmpty(cur) && _state.Profiles.ContainsKey(cur))
            _activeCombo.SelectedItem = cur;
        else if (_state.Profiles.Count > 0)
            _activeCombo.SelectedIndex = 0;
    }

    private async Task AddProfileAsync()
    {
        var dlg = new DevProfileDialog(new DevProfile(), true,
            _state.Settings.BaseUrl, _state.Settings.ModsBaseDir);
        var res = await dlg.ShowDialog<DevProfile>(this);
        if (res == null) return;
        if (_state.Profiles.ContainsKey(res.Key))
        {
            await Dialogs.WarnAsync(this, "Dev", $"Профиль '{res.Key}' уже существует.");
            return;
        }
        if (string.IsNullOrEmpty(res.ModsDir) && !string.IsNullOrEmpty(_state.Settings.ModsBaseDir))
            res.ModsDir = DevStateService.DefaultModsDirFor(_state.Settings.ModsBaseDir, res.Key);
        _state.Profiles[res.Key] = res;
        RefreshProfiles();
        SaveState();
        Log($"Добавлен профиль: {res.Key}");
    }

    private async Task EditProfileAsync()
    {
        var key = _profilesTable.SelectedTag;
        if (key == null) return;
        if (!_state.Profiles.TryGetValue(key, out var old)) return;

        var dlg = new DevProfileDialog(old, false,
            _state.Settings.BaseUrl, _state.Settings.ModsBaseDir);
        var res = await dlg.ShowDialog<DevProfile>(this);
        if (res == null) return;

        res.Mods = old.Mods;
        res.Groups = old.Groups;
        _state.Profiles[key] = res;
        RefreshProfiles();
        SaveState();
        Log($"Обновлён профиль: {key}");
    }

    private void RemoveProfile()
    {
        var key = _profilesTable.SelectedTag;
        if (key == null) return;
        _state.Profiles.Remove(key);
        if (_state.ActiveProfile == key) _state.ActiveProfile = "";
        RefreshProfiles();
        SaveState();
        Log($"Удалён профиль: {key}");
    }

    private void MoveProfile(int delta)
    {
        var key = _profilesTable.SelectedTag;
        if (key == null) return;
        var keys = _state.Profiles.Keys.ToList();
        int i = keys.IndexOf(key);
        int j = i + delta;
        if (j < 0 || j >= keys.Count) return;
        (keys[i], keys[j]) = (keys[j], keys[i]);
        var reordered = new Dictionary<string, DevProfile>();
        foreach (var k in keys) reordered[k] = _state.Profiles[k];
        _state.Profiles = reordered;
        RefreshProfiles();
        _profilesTable.List.SelectedIndex = j;
        SaveState();
    }

    // ---------------------------------------------------------------- //
    //  Mods
    // ---------------------------------------------------------------- //

    private Control BuildModsTab()
    {
        _modsTable = new UiKit.TableBuilder(
            ("Файл",          380),
            ("Размер",         80),
            ("Статус",        110),
            ("По умолч.",      90),
            ("Группы",        140),
            ("Зависит от",    180),
            ("Источник",       90));
        _modsTable.List.SelectionMode = SelectionMode.Multiple;
        _modsTable.List.DoubleTapped += (_, _) => _ = EditModAsync();

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 10, 0, 0)
        };
        btns.Children.Add(MakeButton("Редактировать…",
            () => { _ = EditModAsync(); return Task.CompletedTask; }));
        btns.Children.Add(MakeButton("→ Опциональный",
            () => { BulkMark(true); return Task.CompletedTask; }));
        btns.Children.Add(MakeButton("→ Обязательный",
            () => { BulkMark(false); return Task.CompletedTask; }));

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(btns, Dock.Bottom);
        dock.Children.Add(btns);
        dock.Children.Add(_modsTable.Root);
        return dock;
    }

    private void RefreshMods()
    {
        _modsTable.Clear();
        var key = _activeCombo.SelectedItem as string;
        if (key == null) return;
        if (!_state.Profiles.TryGetValue(key, out var p)) return;

        foreach (var m in p.Mods)
        {
            string sizeStr = m.Size < 1024 * 1024
                ? $"{m.Size / 1024.0:F1} КБ"
                : $"{m.Size / 1024.0 / 1024.0:F1} МБ";
            string status = m.IsOptional ? "Опциональный" : "Обязательный";
            string dflt = m.EnabledOnDefault switch
            {
                true => "вкл",
                false => "выкл",
                _ => "— (группа)"
            };
            string grp = m.Groups.Count > 0 ? string.Join(", ", m.Groups) : "";
            string dep = m.DependsOn.Count > 0 ? string.Join(", ", m.DependsOn) : "";
            _modsTable.AddRow(m.Filename,
                m.Filename, sizeStr, status, dflt, grp, dep, m.Source);
        }
    }

    private async Task EditModAsync()
    {
        var key = _activeCombo.SelectedItem as string;
        if (key == null) return;
        if (!_state.Profiles.TryGetValue(key, out var p)) return;
        var filename = _modsTable.SelectedTag;
        if (filename == null) return;
        var m = p.Mods.FirstOrDefault(x => x.Filename == filename);
        if (m == null) return;

        var others = p.Mods.Where(x => x.Filename != m.Filename).ToList();
        var dlg = new DevModDialog(m, p.Groups.Values.ToList(), others);
        var res = await dlg.ShowDialog<DevMod>(this);
        if (res == null) return;
        RefreshMods();
        SaveState();
        Log($"Изменён {m.Filename}");
    }

    private void BulkMark(bool optional)
    {
        var key = _activeCombo.SelectedItem as string;
        if (key == null) return;
        if (!_state.Profiles.TryGetValue(key, out var p)) return;

        var selected = _modsTable.SelectedTags;
        foreach (var fn in selected)
        {
            var mm = p.Mods.FirstOrDefault(x => x.Filename == fn);
            if (mm != null) mm.IsOptional = optional;
        }

        RefreshMods();
        SaveState();
        Log($"Помечено {(optional ? "опциональными" : "обязательными")}: {selected.Count}");
    }

    // ---------------------------------------------------------------- //
    //  Groups
    // ---------------------------------------------------------------- //

    private Control BuildGroupsTab()
    {
        _groupsTable = new UiKit.TableBuilder(
            ("ID",            160),
            ("Имя",           180),
            ("Режим",         100),
            ("Exclusive-set", 130),
            ("Зависит от",    180),
            ("По умолч.",      80),
            ("Модов",          60));
        _groupsTable.List.SelectionMode = SelectionMode.Single;
        _groupsTable.List.DoubleTapped += (_, _) => _ = EditGroupAsync();

        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 10, 0, 0)
        };
        btns.Children.Add(MakeButton("Добавить…",
            () => { _ = AddGroupAsync(); return Task.CompletedTask; }, primary: true));
        btns.Children.Add(MakeButton("Изменить…",
            () => { _ = EditGroupAsync(); return Task.CompletedTask; }));
        btns.Children.Add(MakeButton("Удалить",
            () => { RemoveGroup(); return Task.CompletedTask; }));

        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(btns, Dock.Bottom);
        dock.Children.Add(btns);
        dock.Children.Add(_groupsTable.Root);
        return dock;
    }

    private void RefreshGroups()
    {
        _groupsTable.Clear();
        var key = _activeCombo.SelectedItem as string;
        if (key == null) return;
        if (!_state.Profiles.TryGetValue(key, out var p)) return;

        foreach (var kv in p.Groups.OrderBy(x => x.Key))
        {
            var gid = kv.Key;
            var g = kv.Value;
            int n = p.Mods.Count(m => m.Groups.Contains(gid));
            string deps = g.DependsOn.Count > 0 ? string.Join(", ", g.DependsOn) : "—";
            _groupsTable.AddRow(gid,
                gid,
                string.IsNullOrEmpty(g.DisplayName) ? "—" : g.DisplayName,
                g.Mode,
                g.ExclusiveSet ?? "—",
                deps,
                g.EnabledOnDefault ? "вкл" : "выкл",
                n.ToString());
        }
    }

    private async Task AddGroupAsync()
    {
        var key = _activeCombo.SelectedItem as string;
        if (key == null) return;
        if (!_state.Profiles.TryGetValue(key, out var p)) return;
        var dlg = new DevGroupDialog(null, p.Groups.Values.ToList());
        var res = await dlg.ShowDialog<DevGroup>(this);
        if (res == null) return;
        if (p.Groups.ContainsKey(res.Id))
        {
            await Dialogs.WarnAsync(this, "Dev", $"Группа '{res.Id}' уже существует.");
            return;
        }
        p.Groups[res.Id] = res;
        RefreshGroups();
        RefreshMods();
        SaveState();
        Log($"Добавлена группа: {res.Id}");
    }

    private async Task EditGroupAsync()
    {
        var key = _activeCombo.SelectedItem as string;
        if (key == null) return;
        if (!_state.Profiles.TryGetValue(key, out var p)) return;
        var gid = _groupsTable.SelectedTag;
        if (gid == null) return;
        if (!p.Groups.TryGetValue(gid, out var g)) return;

        var others = p.Groups.Values.Where(x => x.Id != gid).ToList();
        var dlg = new DevGroupDialog(g, others);
        var res = await dlg.ShowDialog<DevGroup>(this);
        if (res == null) return;

        if (res.Id != gid)
        {
            if (p.Groups.ContainsKey(res.Id))
            {
                await Dialogs.WarnAsync(this, "Dev", $"Группа '{res.Id}' уже существует.");
                return;
            }
            p.Groups.Remove(gid);
            foreach (var m in p.Mods)
            {
                if (m.Groups.Contains(gid))
                    m.Groups = m.Groups.Select(x => x == gid ? res.Id : x).ToList();
                m.DependsOn = m.DependsOn
                    .Select(x => x == gid && !x.EndsWith(".jar") ? res.Id : x).ToList();
            }
            foreach (var gg in p.Groups.Values)
                gg.DependsOn = gg.DependsOn.Select(x => x == gid ? res.Id : x).ToList();
        }
        p.Groups[res.Id] = res;
        RefreshGroups();
        RefreshMods();
        SaveState();
        Log($"Обновлена группа: {gid} → {res.Id}");
    }

    private void RemoveGroup()
    {
        var key = _activeCombo.SelectedItem as string;
        if (key == null) return;
        if (!_state.Profiles.TryGetValue(key, out var p)) return;
        var gid = _groupsTable.SelectedTag;
        if (gid == null) return;

        p.Groups.Remove(gid);
        foreach (var gg in p.Groups.Values)
            gg.DependsOn = gg.DependsOn.Where(x => x != gid).ToList();
        foreach (var m in p.Mods)
        {
            m.Groups = m.Groups.Where(x => x != gid).ToList();
            m.DependsOn = m.DependsOn
                .Where(x => !(x == gid && !x.EndsWith(".jar"))).ToList();
        }
        RefreshGroups();
        RefreshMods();
        SaveState();
        Log($"Удалена группа: {gid}");
    }

    // ---------------------------------------------------------------- //
    //  Flags
    // ---------------------------------------------------------------- //

    private Control BuildFlagsTab()
    {
        var root = new StackPanel { Spacing = 6, Margin = new Thickness(0, 6, 0, 6) };

        var card = UiKit.CardBox("Обход проверок целостности");
        UiKit.AddToCard(card, UiKit.Hint(
            "Флаги сохраняются в dev.flags.json и применяются без перезапуска. " +
            "Влияют на синхронизацию модов при «Установить», «Починить» и «Проверить обновления»."));

        var flags = DevFlags.Snapshot();

        // ---- skip_integrity_check ----
        var chkIntegrity = new CheckBox
        {
            Content = "Пропускать проверку хешей (не перекачивать и не удалять)",
            IsChecked = flags.SkipIntegrityCheck,
            Margin = new Thickness(0, 8, 0, 0)
        };
        chkIntegrity.IsCheckedChanged += (_, _) =>
        {
            bool v = chkIntegrity.IsChecked == true;
            DevFlags.Update(d => d.SkipIntegrityCheck = v);
            Log($"skip_integrity_check = {v}");
        };
        UiKit.AddToCard(card, chkIntegrity);
        UiKit.AddToCard(card, UiKit.Hint(
            "Не перекачивать моды с несовпавшим хешем, не удалять лишние файлы. " +
            "Полезно при правке jar'ов вручную."));

        // ---- skip_missing ----
        var chkMissing = new CheckBox
        {
            Content = "Пропускать отсутствующие моды (не качать, не считать ошибкой)",
            IsChecked = flags.SkipMissing,
            Margin = new Thickness(0, 12, 0, 0)
        };
        chkMissing.IsCheckedChanged += (_, _) =>
        {
            bool v = chkMissing.IsChecked == true;
            DevFlags.Update(d => d.SkipMissing = v);
            Log($"skip_missing = {v}");
        };
        UiKit.AddToCard(card, chkMissing);
        UiKit.AddToCard(card, UiKit.Hint(
            "Для тестов UI: лаунчер не будет скачивать отсутствующие моды."));

        // ---- verbose_log ----
        var chkVerbose = new CheckBox
        {
            Content = "Подробный лог синхронизации",
            IsChecked = flags.VerboseLog,
            Margin = new Thickness(0, 12, 0, 0)
        };
        chkVerbose.IsCheckedChanged += (_, _) =>
        {
            bool v = chkVerbose.IsChecked == true;
            DevFlags.Update(d => d.VerboseLog = v);
            Log($"verbose_log = {v}");
        };
        UiKit.AddToCard(card, chkVerbose);
        UiKit.AddToCard(card, UiKit.Hint(
            "Печатать в лог, какие файлы пропущены и почему."));

        root.Children.Add(card);

        // ---------- Card: локальный output_dir ----------
        var cardLocal = UiKit.CardBox("Тестовый режим (локальные конфиги)");

        var chkLocal = new CheckBox
        {
            Content = "Использовать файлы из output_dir вместо сети",
            IsChecked = flags.UseLocalOutput,
            Margin = new Thickness(0, 8, 0, 0)
        };
        chkLocal.IsCheckedChanged += (_, _) =>
        {
            bool v = chkLocal.IsChecked == true;
            DevFlags.Update(d => d.UseLocalOutput = v);
            Log($"use_local_output = {v}");
        };
        UiKit.AddToCard(cardLocal, chkLocal);
        UiKit.AddToCard(cardLocal, UiKit.Hint(
            "Лаунчер будет читать links.json / profiles.json / manifest-*.json / " +
            "optional-*.json / mods-*.zip из output_dir вместо скачивания с base_url. " +
            "Mojang, NeoForge и Modrinth по-прежнему тянутся из сети."));

        // Показываем, какой каталог будет использован.
        string outputDir = _state.Settings.OutputDir;
        if (string.IsNullOrWhiteSpace(outputDir))
            outputDir = Constants.LauncherDir;
        UiKit.AddToCard(cardLocal, new TextBlock
        {
            Text = $"output_dir: {outputDir}",
            FontFamily = new FontFamily("Consolas, monospace"),
            FontSize = 11,
            Foreground = UiKit.Subtext,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        UiKit.AddToCard(cardLocal, UiKit.Hint(
            "Меняется в Настройках. Файл links.json обязателен — без него " +
            "лаунчер пойдёт в сеть как обычно."));

        root.Children.Add(cardLocal);

        // ---------- Card: служебная инфа ----------
        var card2 = UiKit.CardBox("Файлы");
        UiKit.AddToCard(card2, new TextBlock
        {
            Text = $"dev.flags.json: {DevFlags.FilePath}",
            FontFamily = new FontFamily("Consolas, monospace"),
            FontSize = 11,
            Foreground = UiKit.Subtext,
            TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(card2);

        return root;
    }

    // ---------------------------------------------------------------- //
    //  Log
    // ---------------------------------------------------------------- //

    private Control BuildLogTab()
    {
        _log = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas, JetBrains Mono, monospace"),
            FontSize = 12,
            Background = UiKit.Mantle,
            Foreground = UiKit.Text,
            Padding = new Thickness(12),
            MinHeight = 400
        };
        return _log;
    }

    private void Log(string msg)
    {
        if (_log == null) return;
        Dispatcher.UIThread.Post(() =>
        {
            _log.Text = (_log.Text ?? "") + msg + "\n";
            _log.CaretIndex = _log.Text?.Length ?? 0;
        });
    }

    // ---------------------------------------------------------------- //
    //  Scan
    // ---------------------------------------------------------------- //

    private async Task ScanActiveAsync()
    {
        if (_busy) return;
        var key = _activeCombo.SelectedItem as string;
        if (key == null) return;
        if (!_state.Profiles.TryGetValue(key, out var p)) return;

        if (string.IsNullOrEmpty(p.ModsDir) && !string.IsNullOrEmpty(_state.Settings.ModsBaseDir))
            p.ModsDir = DevStateService.DefaultModsDirFor(_state.Settings.ModsBaseDir, key);
        if (string.IsNullOrEmpty(p.ModsDir))
        {
            await Dialogs.WarnAsync(this, "Dev", "Не задана папка модов у профиля.");
            return;
        }

        var dir = new DirectoryInfo(p.ModsDir);
        if (!dir.Exists) dir.Create();

        // ---- верхний уровень ----
        var jarsTop = dir.EnumerateFiles("*.jar")
                         .OrderBy(f => f.Name)
                         .ToList();

        // ---- подпапка client/ ----
        // Всё, что лежит в <mods_dir>/client/, автоматически считается
        // опциональным. Файл в корне имеет приоритет над одноимённым в client/.
        var clientDir = Path.Combine(dir.FullName, "client");
        var jarsClient = Directory.Exists(clientDir)
            ? new DirectoryInfo(clientDir).EnumerateFiles("*.jar")
                .OrderBy(f => f.Name)
                .ToList()
            : new List<FileInfo>();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tasks = new List<(FileInfo File, bool IsClient)>();

        foreach (var f in jarsTop)
        {
            if (!seen.Add(f.Name)) continue;
            tasks.Add((f, false));
        }
        foreach (var f in jarsClient)
        {
            if (!seen.Add(f.Name))
            {
                Log($"[{key}] ПРЕДУПРЕЖДЕНИЕ: {f.Name} есть и в корне, и в client/. " +
                    $"Использую копию из корня.");
                continue;
            }
            tasks.Add((f, true));
        }

        if (tasks.Count == 0)
        {
            p.Mods = new List<DevMod>();
            RefreshMods();
            SaveState();
            Log($"[{key}] модов 0 (папка {p.ModsDir}).");
            return;
        }

        // ---- строим DevMod из tasks, сохраняя старые поля ----
        var prev = p.Mods.ToDictionary(m => m.Filename, StringComparer.OrdinalIgnoreCase);
        var newMods = new List<DevMod>();

        foreach (var (file, isClient) in tasks)
        {
            if (prev.TryGetValue(file.Name, out var old))
            {
                old.Path = file.FullName;
                old.Size = file.Length;
                if (isClient) old.IsOptional = true;
                newMods.Add(old);
            }
            else
            {
                newMods.Add(new DevMod
                {
                    Filename = file.Name,
                    Path = file.FullName,
                    Size = file.Length,
                    IsOptional = isClient,
                    State = "pending"
                });
            }
        }

        p.Mods = newMods;
        RefreshMods();

        _busy = true;
        var clientNote = jarsClient.Count > 0
            ? $", из них client/: {jarsClient.Count}"
            : "";
        Log($"[{key}] сканирую {p.ModsDir} ({tasks.Count} jar{clientNote})…");

        try
        {
            await Task.Run(async () =>
            {
                foreach (var m in p.Mods)
                {
                    try
                    {
                        await DevModrinthService.ComputeHashesAsync(m);
                        m.State = "resolved";
                    }
                    catch (Exception ex)
                    {
                        m.State = "error";
                        Log($"хеш {m.Filename}: {ex.Message}");
                    }
                }

                var unknown = p.Mods
                    .Where(m => !string.IsNullOrEmpty(m.Sha512)
                                && !_state.ModrinthCache.ContainsKey(m.Sha512))
                    .Select(m => m.Sha512).Distinct().ToList();

                if (unknown.Count > 0)
                {
                    Log($"[Modrinth] новых хешей: {unknown.Count}");
                    var map = await DevModrinthService.LookupAsync(unknown, Log);
                    foreach (var kv in map)
                        _state.ModrinthCache[kv.Key] = kv.Value;
                }

                foreach (var m in p.Mods)
                {
                    ModrinthInfo info;
                    if (!string.IsNullOrEmpty(m.Sha512)
                        && _state.ModrinthCache.TryGetValue(m.Sha512, out info))
                    {
                        m.Source = "modrinth";
                        m.ModrinthUrl = info.Url ?? "";
                        m.ModrinthVersionId = info.VersionId ?? "";
                        m.ModrinthProjectId = info.ProjectId ?? "";
                        if (!string.IsNullOrEmpty(info.Sha1)) m.Sha1 = info.Sha1!;
                    }
                    else m.Source = "unresolved";
                    m.State = "done";
                }

                // ---- подтягиваем title'ы из Modrinth для display_name ----
                var neededPids = p.Mods
                    .Where(m => string.IsNullOrEmpty(m.DisplayName)
                                && m.Source == "modrinth"
                                && !string.IsNullOrEmpty(m.ModrinthProjectId)
                                && !_state.ProjectTitles.ContainsKey(m.ModrinthProjectId))
                    .Select(m => m.ModrinthProjectId)
                    .Distinct()
                    .ToList();

                if (neededPids.Count > 0)
                {
                    Log($"[Modrinth] загружаю названия проектов: {neededPids.Count}");
                    var titles = await DevModrinthService.FetchProjectTitlesAsync(
                        neededPids, Log);
                    foreach (var kv in titles)
                        _state.ProjectTitles[kv.Key] = kv.Value;
                }

                // Заполняем display_name и modrinth_title.
                foreach (var m in p.Mods)
                {
                    if (m.Source != "modrinth") continue;
                    if (string.IsNullOrEmpty(m.ModrinthProjectId)) continue;
                    if (_state.ProjectTitles.TryGetValue(m.ModrinthProjectId, out var title)
                        && !string.IsNullOrEmpty(title))
                    {
                        m.ModrinthTitle = title;
                        if (string.IsNullOrEmpty(m.DisplayName))
                            m.DisplayName = title;
                    }
                }
            });

            Log($"[{key}] сканирование завершено. " +
                $"modrinth: {p.Mods.Count(m => m.Source == "modrinth")}, " +
                $"unresolved: {p.Mods.Count(m => m.Source == "unresolved")}");
            RefreshMods();
            SaveState();
        }
        finally
        {
            _busy = false;
        }
    }

    // ---------------------------------------------------------------- //
    //  Generate
    // ---------------------------------------------------------------- //

    private void GenerateAll()
    {
        ApplySettings();
        try
        {
            DevManifestGenerator.GenerateAll(_state, Log);
            SaveState();
            Log("=== Готово ===");
        }
        catch (Exception ex)
        {
            Log($"ОШИБКА: {ex}");
            _ = Dialogs.ErrorAsync(this, "Dev", ex.Message);
        }
    }

    // ---------------------------------------------------------------- //
    //  Misc
    // ---------------------------------------------------------------- //

    private void OnActiveChanged()
    {
        if (_activeCombo.SelectedItem is string key) _state.ActiveProfile = key;
        RefreshMods();
        RefreshGroups();
    }

    private void SaveState() => DevStateService.Save(_state);

    private void RefreshAll()
    {
        ApplyStateToUi();
        RefreshProfiles();
        RefreshMods();
        RefreshGroups();
    }

    private void ApplyStateToUi()
    {
        _baseUrl.Text = _state.Settings.BaseUrl;
        _launcherVer.Text = _state.Settings.LauncherVersion;
        _launcherDl.Text = _state.Settings.LauncherDownloadUrl;
        _manifestVer.Text = _state.Settings.ManifestVersion;
        _outputDir.Text = _state.Settings.OutputDir;
        _modsBaseDir.Text = _state.Settings.ModsBaseDir;
    }
}