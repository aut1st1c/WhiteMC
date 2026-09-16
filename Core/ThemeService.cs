using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;

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
        {
            if (string.Equals(tid, id, StringComparison.OrdinalIgnoreCase))
                return tid;
        }
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
    }

    private static bool TryApply(string themeId, Application app)
    {
        try
        {
            // Удаляем ранее загруженные темы (по пути в URI).
            for (int i = app.Styles.Count - 1; i >= 0; i--)
            {
                if (app.Styles[i] is StyleInclude si
                    && si.Source?.OriginalString.Contains("/Themes/", StringComparison.OrdinalIgnoreCase) == true)
                {
                    app.Styles.RemoveAt(i);
                }
            }

            // StyleInclude в Avalonia 11 заменяет ResourceInclude для стилей.
            var include = new StyleInclude(new Uri("avares://WhiteMC/"))
            {
                Source = new Uri($"avares://WhiteMC/Themes/{themeId}.axaml")
            };
            app.Styles.Add(include);

            return true;
        }
        catch (Exception ex)
        {
            LogService.Log($"[WARN] TryApply('{themeId}') упал: {ex.Message}");
            return false;
        }
    }
}