using System.Threading.Tasks;
using Avalonia.Controls;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;

namespace WhiteMC;

/// <summary>
/// Тонкая обёртка над MessageBox.Avalonia, чтобы не тащить его API по всему коду
/// и корректно работать с null-owner (например, когда окно ещё не создано).
/// </summary>
public static class Dialogs
{
    public static async Task InfoAsync(Window? owner, string title, string message)
        => await ShowAsync(owner, title, message, ButtonEnum.Ok, Icon.Info);

    public static async Task WarnAsync(Window? owner, string title, string message)
        => await ShowAsync(owner, title, message, ButtonEnum.Ok, Icon.Warning);

    public static async Task ErrorAsync(Window? owner, string title, string message)
        => await ShowAsync(owner, title, message, ButtonEnum.Ok, Icon.Error);

    public static async Task<bool> ConfirmAsync(Window? owner, string title, string message)
        => await ShowAsync(owner, title, message, ButtonEnum.YesNo, Icon.Question) == ButtonResult.Yes;

    private static async Task<ButtonResult> ShowAsync(
        Window? owner, string title, string message, ButtonEnum buttons, Icon icon)
    {
        var box = MessageBoxManager.GetMessageBoxStandard(title, message, buttons, icon);

        // ShowWindowDialogAsync требует непустой owner. Если окно ещё не создано —
        // используем ShowAsync (отдельное top-level окно).
        if (owner is not null && owner.IsVisible)
            return await box.ShowWindowDialogAsync(owner);

        return await box.ShowAsync();
    }
}