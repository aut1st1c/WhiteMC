using System;
using System.Collections.Generic;
using System.Windows;

namespace WhiteMC.Core;

public static class ThemeService
{
    public static readonly IReadOnlyList<(string Id, string Name)> Available = new[]
    {
        ("Mocha",    "Mocha — синяя тёмная"),
        ("Latte",    "Latte — светлая"),
        ("Obsidian", "Obsidian — OLED + циан"),
        ("Ember",    "Ember — тёплая, янтарь"),
        ("Neon",     "Neon — киберпанк"),
    };

    public const string DefaultTheme = "Mocha";

    public static string CurrentId { get; private set; } = DefaultTheme;

    public static string Normalize(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return DefaultTheme;
        foreach (var (tid, _) in Available)
            if (string.Equals(tid, id, StringComparison.OrdinalIgnoreCase))
                return tid;
        return DefaultTheme;
    }

    public static void Apply(string? id)
    {
        var requested = Normalize(id);
        var app = Application.Current;
        if (app == null) return;

        if (!TryApply(requested, app))
        {
            if (requested == DefaultTheme) return;
            LogService.Log($"[WARN] Тема '{requested}' не загрузилась, откат на {DefaultTheme}");
            if (!TryApply(DefaultTheme, app)) return;
            requested = DefaultTheme;
        }

        CurrentId = requested;
        LogService.Log($"[WhiteMC] Применена тема: {requested}");
        KickWindows(app);
    }

    private static bool TryApply(string themeId, Application app)
    {
        try
        {
            var merged = app.Resources.MergedDictionaries;

            for (int i = merged.Count - 1; i >= 0; i--)
            {
                var src = merged[i].Source?.OriginalString ?? "";
                if (src.StartsWith("Themes/", StringComparison.OrdinalIgnoreCase))
                    merged.RemoveAt(i);
            }

            merged.Add(new ResourceDictionary
            {
                Source = new Uri($"Themes/{themeId}.xaml", UriKind.Relative)
            });

            return true;
        }
        catch (Exception ex)
        {
            LogService.Log($"[WARN] TryApply('{themeId}') упал: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Заставляет WPF переприменить implicit-стили к уже открытым окнам.
    /// Нужно, потому что при подмене MergedDictionaries стили без x:Key
    /// не всегда триггерят обновление визуального дерева.
    /// </summary>
    private static void KickWindows(Application app)
    {
        foreach (Window w in app.Windows)
        {
            try
            {
                var s = w.Style;
                w.Style = null;
                w.Style = s;
                w.InvalidateVisual();
                w.UpdateLayout();
            }
            catch { }
        }
    }
}