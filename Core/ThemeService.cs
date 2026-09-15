using System;
using System.Collections.Generic;
using System.Windows;

namespace WhiteMC.Core;

public static class ThemeService
{
    public static readonly IReadOnlyList<(string Id, string Name)> Available = new[]
    {
        ("Mocha",      "Catppuccin Mocha"),
        ("Latte",      "Catppuccin Latte"),
        ("Nord",       "Nord"),
        ("Dracula",    "Dracula"),
        ("TokyoNight", "Tokyo Night"),
        ("Gruvbox",    "Gruvbox Dark"),
    };

    public const string DefaultTheme = "Mocha";

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

        // 1) Пробуем запрошенную тему, при провале — Mocha.
        if (TryApply(requested, app)) return;

        LogService.Log($"[WARN] Тема '{requested}' не загрузилась, откат на {DefaultTheme}");
        if (requested != DefaultTheme && TryApply(DefaultTheme, app)) return;

        LogService.Log("[FATAL] Не удалось загрузить ни одну тему — окна будут без стилей");
    }

    private static bool TryApply(string themeId, Application app)
    {
        try
        {
            var merged = app.Resources.MergedDictionaries;

            // Убираем предыдущие темы (актуально при повторном вызове).
            for (int i = merged.Count - 1; i >= 0; i--)
            {
                var src = merged[i].Source?.OriginalString ?? "";
                if (src.Contains("/Themes/", StringComparison.OrdinalIgnoreCase) ||
                    src.StartsWith("Themes/", StringComparison.OrdinalIgnoreCase))
                {
                    merged.RemoveAt(i);
                }
            }

            merged.Add(new ResourceDictionary
            {
                Source = new Uri($"Themes/Colors/{themeId}.xaml", UriKind.Relative)
            });
            merged.Add(new ResourceDictionary
            {
                Source = new Uri("Themes/Base.xaml", UriKind.Relative)
            });

            LogService.Log($"[WhiteMC] Применена тема: {themeId}");
            return true;
        }
        catch (Exception ex)
        {
            LogService.Log($"[WARN] TryApply('{themeId}') упал: {ex.Message}");
            return false;
        }
    }
}