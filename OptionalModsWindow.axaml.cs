using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using WhiteMC.Core;

namespace WhiteMC;

public partial class OptionalModsWindow : Window
{
    private readonly string _profile;
    private readonly OptionalManifest _manifest;
    private OptionalState _state;
    private bool _loading;

    private Dictionary<string, OptionalMod> _byName = new(StringComparer.OrdinalIgnoreCase);

    public OptionalModsWindow()
        : this("default", new OptionalManifest()) { }

    public OptionalModsWindow(string profile, OptionalManifest manifest)
    {
        InitializeComponent();
        _profile = profile;
        _manifest = manifest;
        _state = OptionalModsService.LoadState(profile);
        IndexMods();
        Rebuild();
    }

    // ------------------------------------------------------------ //
    //  Утилиты
    // ------------------------------------------------------------ //

    private void IndexMods()
    {
        _byName = new Dictionary<string, OptionalMod>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in _manifest.Mods)
            _byName[m.Filename] = m;
    }

    private OptionalMod? FindMod(string filename)
        => _byName.TryGetValue(filename, out var m) ? m : null;

    /// <summary>
    /// Все моды, у которых gid встречается в groups (любая позиция).
    /// Для UI (Rebuild) по-прежнему используем Groups[0], чтобы мод
    /// не отображался дважды.
    /// </summary>
    private List<OptionalMod> ModsInGroup(string gid)
        => _manifest.Mods
            .Where(m => m.Groups != null && m.Groups.Count > 0
                        && m.Groups.Contains(gid, StringComparer.OrdinalIgnoreCase))
            .ToList();

    private bool IsGroupEnabled(string gid)
        => ModsInGroup(gid).Any(m => OptionalModsService.IsEnabled(_state, m.Filename));

    private string GetGroupDisplay(string gid)
        => _manifest.Groups.TryGetValue(gid, out var g)
           && !string.IsNullOrWhiteSpace(g.DisplayName) ? g.DisplayName! : gid;

    private static string GetDisplay(OptionalMod mod)
    {
        if (!string.IsNullOrWhiteSpace(mod.DisplayName)) return mod.DisplayName!;
        if (!string.IsNullOrWhiteSpace(mod.Label))       return mod.Label!;
        return mod.Filename;
    }

    private string DepDisplay(string entry)
    {
        if (_byName.TryGetValue(entry, out var m)) return GetDisplay(m);
        if (_manifest.Groups.ContainsKey(entry))   return GetGroupDisplay(entry);
        return entry;
    }

    // ------------------------------------------------------------ //
    //  Сбор «что включить»
    // ------------------------------------------------------------ //

    private List<OptionalMod> BuildEnableOrder(OptionalMod root)
    {
        var result     = new List<OptionalMod>();
        var seenMods   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddDep(string entry)
        {
            if (string.IsNullOrWhiteSpace(entry)) return;

            if (_byName.ContainsKey(entry))          { AddMod(_byName[entry]); return; }
            if (_manifest.Groups.ContainsKey(entry)) { AddGroup(entry);        return; }
        }

        void AddGroup(string gid)
        {
            if (!seenGroups.Add(gid)) return;
            if (!_manifest.Groups.TryGetValue(gid, out var g)) return;

            foreach (var dep in g.DependsOn ?? new List<string>())
                AddDep(dep);

            foreach (var m in ModsInGroup(gid))
                AddMod(m);
        }

        void AddMod(OptionalMod m)
        {
            if (!seenMods.Add(m.Filename)) return;

            if (m.Groups != null)
                foreach (var g in m.Groups)
                {
                    if (_manifest.Groups.TryGetValue(g, out var grp) && grp.DependsOn != null)
                        foreach (var dep in grp.DependsOn)
                            AddDep(dep);
                }

            if (m.DependsOn != null)
                foreach (var dep in m.DependsOn)
                    AddDep(dep);

            result.Add(m);
        }

        AddMod(root);
        return result;
    }

    // ------------------------------------------------------------ //
    //  Сбор «что выключить»
    // ------------------------------------------------------------ //

    /// <summary>
    /// Расширяет набор выключаемых модов транзитивными зависимыми.
    /// Уже выключенные моды в набор не попадают — их и так нет.
    /// </summary>
    private List<OptionalMod> ExpandDisableSet(
        IReadOnlyCollection<OptionalMod> roots,
        IReadOnlyCollection<string> rootGroups)
    {
        var modsToDisable   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groupsToDisable = new HashSet<string>(rootGroups, StringComparer.OrdinalIgnoreCase);

        // Сами roots — если они были включены.
        foreach (var m in roots)
            if (OptionalModsService.IsEnabled(_state, m.Filename))
                modsToDisable.Add(m.Filename);

        // Все включённые моды выключаемых групп.
        foreach (var g in groupsToDisable.ToList())
            foreach (var m in ModsInGroup(g))
                if (OptionalModsService.IsEnabled(_state, m.Filename))
                    modsToDisable.Add(m.Filename);

        // Группы всех выключаемых модов.
        foreach (var m in roots)
            foreach (var g in m.Groups ?? new List<string>())
                groupsToDisable.Add(g);

        // Fixed-point: транзитивные зависимые.
        bool changed = true;
        while (changed)
        {
            changed = false;

            foreach (var mod in _manifest.Mods)
            {
                if (modsToDisable.Contains(mod.Filename)) continue;
                if (!OptionalModsService.IsEnabled(_state, mod.Filename)) continue;

                bool hit = false;

                if (mod.DependsOn != null)
                {
                    foreach (var d in mod.DependsOn)
                    {
                        if (_byName.ContainsKey(d))
                        {
                            if (modsToDisable.Contains(d)) { hit = true; break; }
                        }
                        else if (_manifest.Groups.ContainsKey(d))
                        {
                            if (groupsToDisable.Contains(d)) { hit = true; break; }
                        }
                    }
                }

                if (!hit && mod.Groups != null)
                {
                    foreach (var g in mod.Groups)
                    {
                        if (_manifest.Groups.TryGetValue(g, out var grp) && grp.DependsOn != null)
                            foreach (var d in grp.DependsOn)
                                if (groupsToDisable.Contains(d)) { hit = true; break; }
                        if (hit) break;
                    }
                }

                if (hit)
                {
                    modsToDisable.Add(mod.Filename);
                    foreach (var g in mod.Groups ?? new List<string>())
                        groupsToDisable.Add(g);
                    changed = true;
                }
            }

            foreach (var (gid, grp) in _manifest.Groups)
            {
                if (groupsToDisable.Contains(gid)) continue;
                if (grp.DependsOn == null) continue;

                bool hit = grp.DependsOn.Any(d => groupsToDisable.Contains(d));
                if (hit)
                {
                    groupsToDisable.Add(gid);
                    foreach (var m in ModsInGroup(gid))
                        if (OptionalModsService.IsEnabled(_state, m.Filename))
                            modsToDisable.Add(m.Filename);
                    changed = true;
                }
            }
        }

        return _manifest.Mods.Where(m => modsToDisable.Contains(m.Filename)).ToList();
    }

    /// <summary>
    /// Выключает roots + все их транзитивные зависимые.
    /// Без диалогов: снятая галочка = ожидание, что связанные моды тоже уйдут.
    /// </summary>
    private async Task<bool> TryDisableWithDependentsAsync(
        List<OptionalMod> roots, List<string> rootGroups)
    {
        var all = ExpandDisableSet(roots, rootGroups);

        await Task.Run(async () =>
        {
            foreach (var m in all)
                await OptionalModsService.SetEnabledAsync(_profile, m, false, LogService.Log);
        });
        return true;
    }

    private async Task EnableWithDepsAsync(OptionalMod root)
    {
        var order = BuildEnableOrder(root);
        await Task.Run(async () =>
        {
            foreach (var m in order)
                await OptionalModsService.SetEnabledAsync(_profile, m, true, LogService.Log);
        });
    }

    private async Task EnableGroupWithDepsAsync(string gid)
    {
        var first = ModsInGroup(gid).FirstOrDefault();
        if (first == null) return;

        var order = BuildEnableOrder(first);

        await Task.Run(async () =>
        {
            foreach (var m in order)
                await OptionalModsService.SetEnabledAsync(_profile, m, true, LogService.Log);

            foreach (var m in ModsInGroup(gid))
                await OptionalModsService.SetEnabledAsync(_profile, m, true, LogService.Log);

            if (_manifest.Groups.TryGetValue(gid, out var grp)
                && !string.IsNullOrWhiteSpace(grp.ExclusiveSet))
            {
                await DisableExclusiveNeighborsExceptAsync(grp.ExclusiveSet!, gid);
            }
        });
    }

    /// <summary>
    /// Гасит соседей по exclusive_set, кроме currentGid.
    /// Моды, которые входят и в currentGid тоже, не трогаем — они общие.
    /// </summary>
    private async Task DisableExclusiveNeighborsExceptAsync(string exclusiveSet, string currentGid)
    {
        foreach (var (otherGid, otherGrp) in _manifest.Groups)
        {
            if (string.Equals(otherGid, currentGid, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(otherGrp.ExclusiveSet, exclusiveSet,
                               StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var m in ModsInGroup(otherGid))
            {
                if (m.Groups != null && m.Groups.Any(g =>
                        string.Equals(g, currentGid, StringComparison.OrdinalIgnoreCase)))
                    continue;

                if (!OptionalModsService.IsEnabled(_state, m.Filename)) continue;
                await OptionalModsService.SetEnabledAsync(_profile, m, false, LogService.Log);
            }
        }
    }

    // ------------------------------------------------------------ //
    //  Rebuild / рендер
    // ------------------------------------------------------------ //

    private void Rebuild()
    {
        _loading = true;
        LstMods.Children.Clear();
        IndexMods();

        if (_manifest.Mods.Count == 0)
        {
            LstMods.Children.Add(new TextBlock
            {
                Text = "Манифест опциональных модов пуст.",
                Classes = { "subtext" },
                Margin = new Thickness(8, 12, 8, 12)
            });
            _loading = false;
            return;
        }

        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buckets = new List<(string Gid, OptionalGroup Group, List<OptionalMod> Members)>();

        foreach (var mod in _manifest.Mods)
        {
            if (processed.Contains(mod.Filename)) continue;
            if (mod.Groups == null || mod.Groups.Count == 0) continue;

            var gid = mod.Groups[0];
            var grp = _manifest.Groups.TryGetValue(gid, out var g)
                ? g
                : new OptionalGroup { DisplayName = gid };

            var members = new List<OptionalMod> { mod };
            processed.Add(mod.Filename);

            foreach (var m2 in _manifest.Mods)
            {
                if (processed.Contains(m2.Filename)) continue;
                if (m2.Groups != null && m2.Groups.Contains(gid, StringComparer.OrdinalIgnoreCase))
                {
                    members.Add(m2);
                    processed.Add(m2.Filename);
                }
            }

            buckets.Add((gid, grp, members));
        }

        buckets.Sort((a, b) =>
        {
            bool aEx = !string.IsNullOrWhiteSpace(a.Group.ExclusiveSet);
            bool bEx = !string.IsNullOrWhiteSpace(b.Group.ExclusiveSet);
            if (aEx != bEx) return aEx ? 1 : -1;
            if (aEx && bEx)
            {
                int c = string.Compare(a.Group.ExclusiveSet, b.Group.ExclusiveSet,
                                       StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
            }
            return 0;
        });

        string? prevSet = null;
        foreach (var (gid, grp, members) in buckets)
        {
            if (!string.IsNullOrWhiteSpace(grp.ExclusiveSet) &&
                !string.Equals(prevSet, grp.ExclusiveSet, StringComparison.OrdinalIgnoreCase))
            {
                LstMods.Children.Add(RenderExclusiveSetHeader(grp.ExclusiveSet!, buckets));
                prevSet = grp.ExclusiveSet;
            }

            LstMods.Children.Add(RenderGroup(gid, grp, members));
        }

        foreach (var mod in _manifest.Mods)
        {
            if (processed.Contains(mod.Filename)) continue;
            LstMods.Children.Add(RenderStandalone(mod));
        }

        _loading = false;
    }

    private static Control RenderExclusiveSetHeader(
        string setName, List<(string Gid, OptionalGroup Group, List<OptionalMod> Members)> buckets)
    {
        var groupNames = buckets
            .Where(b => string.Equals(b.Group.ExclusiveSet, setName, StringComparison.OrdinalIgnoreCase))
            .Select(b => string.IsNullOrWhiteSpace(b.Group.DisplayName) ? b.Gid : b.Group.DisplayName!)
            .ToList();

        var panel = new StackPanel { Margin = new Thickness(4, 14, 4, 4) };
        panel.Children.Add(new TextBlock
        {
            Text = $"═══ Выберите один из: {string.Join(" / ", groupNames)} ═══",
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#6c9cff"))
        });
        return panel;
    }

    private Control RenderGroup(string gid, OptionalGroup grp, List<OptionalMod> members)
    {
        var panel = new StackPanel { Margin = new Thickness(4, 8, 4, 8) };
        var mode  = (grp.Mode ?? "grouped").Trim().ToLowerInvariant();
        var title = string.IsNullOrWhiteSpace(grp.DisplayName) ? gid : grp.DisplayName!;

        switch (mode)
        {
            case "bundle":    RenderBundle(panel, gid, title, grp, members);    break;
            case "exclusive": RenderExclusive(panel, gid, title, grp, members); break;
            default:          RenderGrouped(panel, gid, title, grp, members);   break;
        }

        return panel;
    }

    private void RenderBundle(StackPanel panel, string gid, string title,
                              OptionalGroup grp, List<OptionalMod> members)
    {
        bool allEnabled = members.All(m => OptionalModsService.IsEnabled(_state, m.Filename));

        var chk = new CheckBox
        {
            Content    = title,
            IsChecked  = allEnabled,
            FontSize   = 13,
            FontWeight = FontWeight.SemiBold,
            Tag        = new BundleTag(gid, members, grp.ExclusiveSet)
        };
        chk.IsCheckedChanged += OnBundleToggle;
        panel.Children.Add(chk);

        if (!string.IsNullOrWhiteSpace(grp.Description))
            panel.Children.Add(SubText(grp.Description!, indent: 28, top: 2));

        RenderGroupDepHint(panel, grp, indent: 28);
        // Список модов группы не показываем — для bundle он не нужен.
    }

    private void RenderExclusive(StackPanel panel, string gid, string title,
                                 OptionalGroup grp, List<OptionalMod> members)
    {
        panel.Children.Add(new TextBlock
        {
            Text = title, FontWeight = FontWeight.SemiBold, FontSize = 13
        });

        if (!string.IsNullOrWhiteSpace(grp.Description))
            panel.Children.Add(SubText(grp.Description!, indent: 0, top: 2));

        RenderGroupDepHint(panel, grp, indent: 0);

        bool anyEnabled = members.Any(m => OptionalModsService.IsEnabled(_state, m.Filename));

        var none = new RadioButton
        {
            Content   = "Не использовать",
            GroupName = "grp_" + gid,
            IsChecked = !anyEnabled,
            FontSize  = 12,
            Margin    = new Thickness(0, 4, 0, 0),
            Tag       = new ExclusiveTag(gid, null, members)
        };
        none.IsCheckedChanged += OnExclusiveToggle;
        panel.Children.Add(none);

        foreach (var mod in members)
        {
            bool enabled = OptionalModsService.IsEnabled(_state, mod.Filename);

            var rb = new RadioButton
            {
                Content   = GetDisplay(mod),
                GroupName = "grp_" + gid,
                IsChecked = enabled,
                FontSize  = 13,
                Margin    = new Thickness(0, 4, 0, 0),
                Tag       = new ExclusiveTag(gid, mod, members)
            };
            rb.IsCheckedChanged += OnExclusiveToggle;
            panel.Children.Add(rb);

            if (!string.IsNullOrWhiteSpace(mod.Description))
                panel.Children.Add(SubText(mod.Description!, indent: 24, top: 2));

            RenderModDepHint(panel, mod, indent: 24);
        }
    }

    private void RenderGrouped(StackPanel panel, string gid, string title,
                               OptionalGroup grp, List<OptionalMod> members)
    {
        panel.Children.Add(new TextBlock
        {
            Text = title, FontWeight = FontWeight.SemiBold, FontSize = 13,
            Margin = new Thickness(0, 0, 0, 4)
        });

        if (!string.IsNullOrWhiteSpace(grp.Description))
            panel.Children.Add(SubText(grp.Description!, indent: 0, top: 0, bottom: 4));

        RenderGroupDepHint(panel, grp, indent: 0);

        foreach (var mod in members)
            panel.Children.Add(RenderModCheckbox(mod, gid));
    }

    private Control RenderStandalone(OptionalMod mod)
    {
        var panel = new StackPanel { Margin = new Thickness(4, 6, 4, 6) };
        panel.Children.Add(RenderModCheckbox(mod, null));

        if (!string.IsNullOrWhiteSpace(mod.Description))
            panel.Children.Add(SubText(mod.Description!, indent: 28, top: 2));

        RenderModDepHint(panel, mod, indent: 28);
        return panel;
    }

    private void RenderGroupDepHint(StackPanel panel, OptionalGroup grp, double indent)
    {
        if (grp.DependsOn == null || grp.DependsOn.Count == 0) return;

        var known = grp.DependsOn.Where(d => _manifest.Groups.ContainsKey(d)).ToList();
        if (known.Count == 0) return;

        RenderDepLine(panel, "Зависит от", known, indent);
    }

    private void RenderModDepHint(StackPanel panel, OptionalMod mod, double indent)
    {
        if (mod.DependsOn == null || mod.DependsOn.Count == 0) return;

        var known = mod.DependsOn
            .Where(d => _byName.ContainsKey(d) || _manifest.Groups.ContainsKey(d))
            .ToList();
        if (known.Count == 0) return;

        RenderDepLine(panel, "Требует", known, indent);
    }

    private void RenderDepLine(StackPanel panel, string label,
                               IEnumerable<string> entries, double indent)
    {
        var list = entries.ToList();
        var missing = list.Where(d =>
        {
            if (_byName.TryGetValue(d, out var m))
                return !OptionalModsService.IsEnabled(_state, m.Filename);
            if (_manifest.Groups.ContainsKey(d))
                return !IsGroupEnabled(d);
            return false;
        }).ToList();

        string text;
        IBrush brush;
        if (missing.Count == 0)
        {
            text = $"✓ {label}: {string.Join(", ", list.Select(DepDisplay))}";
            brush = new SolidColorBrush(Color.Parse("#5a9b5a"));
        }
        else
        {
            text = $"⚠ {label}: {string.Join(", ", missing.Select(DepDisplay))} " +
                   $"(включится автоматически)";
            brush = new SolidColorBrush(Color.Parse("#c9a227"));
        }

        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = brush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(indent, 2, 0, 0)
        });
    }

    private CheckBox RenderModCheckbox(OptionalMod mod, string? gid)
    {
        bool enabled = OptionalModsService.IsEnabled(_state, mod.Filename);
        bool present = OptionalModsService.IsPresentOnDisk(_profile, mod.Filename);

        string info = present
            ? GetDisplay(mod)
            : GetDisplay(mod) + (enabled
                ? "  •  не скачан — будет загружен при включении"
                : "  •  не скачан");

        var chk = new CheckBox
        {
            Content = info,
            IsChecked = enabled,
            Tag = new ModToggleTag(mod, gid),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Top
        };
        chk.IsCheckedChanged += OnModToggle;
        return chk;
    }

    private static TextBlock SubText(string text, double indent, double top,
                                      double bottom = 0, double size = 12)
        => new()
        {
            Text = text,
            Classes = { "subtext" },
            FontSize = size,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(indent, top, 0, bottom)
        };

    // ------------------------------------------------------------ //
    //  Handlers
    // ------------------------------------------------------------ //

    private async void OnBundleToggle(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not CheckBox chk || chk.Tag is not BundleTag tag) return;

        bool enable = chk.IsChecked == true;
        chk.IsEnabled = false;

        try
        {
            if (enable)
            {
                LblStatus.Text = $"Включение «{GetGroupDisplay(tag.Gid)}»…";
                await EnableGroupWithDepsAsync(tag.Gid);
            }
            else
            {
                await TryDisableWithDependentsAsync(tag.Members, new List<string> { tag.Gid });
            }

            _state = OptionalModsService.LoadState(_profile);
            LblStatus.Text = "";
            Rebuild();
        }
        catch (Exception ex)
        {
            LogService.Log($"[Optional] ошибка переключения группы: {ex.Message}");
            LblStatus.Text = "";
            await Dialogs.ErrorAsync(this, "WhiteMC", ex.Message);

            _loading = true;
            chk.IsChecked = !enable;
            _loading = false;
        }
        finally { chk.IsEnabled = true; }
    }

    private async void OnExclusiveToggle(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not RadioButton rb || rb.IsChecked != true) return;
        if (rb.Tag is not ExclusiveTag tag) return;

        var gid    = tag.Gid;
        var chosen = tag.Chosen;

        LblStatus.Text = "Применение выбора…";
        try
        {
            if (chosen == null)
            {
                await TryDisableWithDependentsAsync(tag.Members, new List<string> { gid });
            }
            else
            {
                await Task.Run(async () =>
                {
                    var order = BuildEnableOrder(chosen);
                    foreach (var m in order)
                        await OptionalModsService.SetEnabledAsync(_profile, m, true, LogService.Log);

                    foreach (var m in tag.Members)
                    {
                        if (string.Equals(m.Filename, chosen.Filename,
                                          StringComparison.OrdinalIgnoreCase)) continue;
                        await OptionalModsService.SetEnabledAsync(_profile, m, false, LogService.Log);
                    }

                    if (_manifest.Groups.TryGetValue(gid, out var g)
                        && !string.IsNullOrWhiteSpace(g.ExclusiveSet))
                    {
                        await DisableExclusiveNeighborsExceptAsync(g.ExclusiveSet!, gid);
                    }
                });
            }

            _state = OptionalModsService.LoadState(_profile);
            LblStatus.Text = "";
            Rebuild();
        }
        catch (Exception ex)
        {
            LogService.Log($"[Optional] ошибка exclusive-группы: {ex.Message}");
            LblStatus.Text = "";
            await Dialogs.ErrorAsync(this, "WhiteMC", ex.Message);
        }
    }

    private async void OnModToggle(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not CheckBox chk || chk.Tag is not ModToggleTag tag) return;

        var mod = tag.Mod;
        var gid = tag.Gid;
        bool enable = chk.IsChecked == true;

        chk.IsEnabled = false;
        LblStatus.Text = enable
            ? $"Включение {GetDisplay(mod)}…"
            : $"Отключение {GetDisplay(mod)}…";

        try
        {
            if (enable)
            {
                await EnableWithDepsAsync(mod);

                if (!string.IsNullOrEmpty(gid)
                    && _manifest.Groups.TryGetValue(gid!, out var grp)
                    && !string.IsNullOrWhiteSpace(grp.ExclusiveSet))
                {
                    await DisableExclusiveNeighborsExceptAsync(grp.ExclusiveSet!, gid!);
                }
            }
            else
            {
                var rootGroups = new List<string>();
                if (!string.IsNullOrEmpty(gid))
                {
                    int stillEnabled = ModsInGroup(gid!)
                        .Count(m => !string.Equals(m.Filename, mod.Filename,
                                                   StringComparison.OrdinalIgnoreCase)
                                    && OptionalModsService.IsEnabled(_state, m.Filename));
                    if (stillEnabled == 0)
                        rootGroups.Add(gid!);
                }

                await TryDisableWithDependentsAsync(new List<OptionalMod> { mod }, rootGroups);
            }

            _state = OptionalModsService.LoadState(_profile);
            LblStatus.Text = "";
            Rebuild();
        }
        catch (Exception ex)
        {
            LogService.Log($"[Optional] ошибка переключения {mod.Filename}: {ex.Message}");
            LblStatus.Text = "";
            await Dialogs.ErrorAsync(this, "WhiteMC", ex.Message);

            _loading = true;
            chk.IsChecked = !enable;
            _loading = false;
        }
        finally { chk.IsEnabled = true; }
    }

    private void BtnOpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.Combine(InstanceManager.GetDir(_profile), "mods");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch { }
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            try { BeginMoveDrag(e); } catch { }
        }
    }

    private void BtnMinimize_Click(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void BtnClose_Click(object? sender, RoutedEventArgs e) => Close();

    private sealed record BundleTag(string Gid, List<OptionalMod> Members, string? ExclusiveSet);
    private sealed record ExclusiveTag(string Gid, OptionalMod? Chosen, List<OptionalMod> Members);
    private sealed record ModToggleTag(OptionalMod Mod, string? Gid);
}