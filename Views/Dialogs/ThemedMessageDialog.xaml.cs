using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LenSa.Core.Services.Theme;

namespace LenSa.Core.Views.Dialogs;

public enum DialogButtonKind { Primary, Danger, Normal }

public sealed record DialogButton(string Text, MessageBoxResult Result,
    DialogButtonKind Kind = DialogButtonKind.Normal);

public sealed record ThemedDialogRequest(string Title, string Message, MessageBoxImage Icon,
    MessageBoxResult CancelResult, IReadOnlyList<DialogButton> Buttons);

public partial class ThemedMessageDialog : Window
{
    public static Func<ThemedDialogRequest, MessageBoxResult>? Override;

    private MessageBoxResult _result = MessageBoxResult.Cancel;

    public ThemedMessageDialog()
    {
        InitializeComponent();
        WindowCaptionTheme.Attach(this);
        Loaded += (_, _) => (ButtonsPanel.Children.OfType<Button>().FirstOrDefault())?.Focus();
    }

    public ThemedMessageDialog(ThemedDialogRequest request) : this()
    {
        Title = request.Title;
        MessageText.Text = request.Message;
        _result = request.CancelResult;
        IconText.Text = request.Icon == MessageBoxImage.Error ? "!" : "?";
        if (request.Icon == MessageBoxImage.Error)
            IconCircle.SetResourceReference(Border.BackgroundProperty, "ErrorColor");
        foreach (var definition in request.Buttons)
        {
            var button = new Button
            {
                Content = definition.Text,
                IsDefault = ButtonsPanel.Children.Count == 0,
                Style = (Style)Resources[definition.Kind + "Button"]
            };
            button.Click += (_, _) => { _result = definition.Result; Close(); };
            ButtonsPanel.Children.Add(button);
        }
    }

    public static MessageBoxResult Show(Window? owner, string title, string message,
        MessageBoxImage icon, MessageBoxResult cancelResult, params DialogButton[] buttons)
    {
        var request = new ThemedDialogRequest(title, message, icon, cancelResult, buttons);
        if (Override is { } callback) return callback(request);

        owner ??= Application.Current?.Windows.OfType<Window>()
            .FirstOrDefault(window => window.IsVisible && window.IsActive)
            ?? Application.Current?.MainWindow;
        var dialog = new ThemedMessageDialog(request);
        if (owner != null) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
        return dialog._result;
    }

    private void Dialog_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }
}
