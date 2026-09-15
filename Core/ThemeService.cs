using System;
using System.Collections.Generic;
using System.Windows;

namespace WhiteMC.Core;

public static class ThemeService
{
    /// <summary>Список доступных тем: (id файла, человекочитаемое название).</summary>
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

    /// <summary>
    /// Перестраивает Application.Resources: подмешивает Colors/{id}.xaml и Base.xaml.
    /// Вызывать ДО создания окон.
    /// </summary>
    public static void Apply(string? id)
    {
        var themeId = Normalize(id);

        var app = Application.Current;
        if (app == null)
        {
            // Консольный/юнит-тест — просто игнорируем.
            return;
        }

        var merged = app.Resources.MergedDictionaries;

        // Убираем предыдущие темы (на случай повторного вызова).
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
    }
}