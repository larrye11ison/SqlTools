using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using CommunityToolkit.Mvvm.Messaging;
using SqlPhanos.Enums;
using SqlPhanos.Messages;

namespace SqlPhanos.Views;

public partial class SearchView : UserControl
{
    public SearchView()
    {
        InitializeComponent();

        if (this.FindControl<Control>("DeleteConfirmationOverlay") is { } deleteOverlay &&
            this.FindControl<Button>("CancelDeleteButton") is { } cancelButton)
        {
            OverlayFocusHelper.FocusOnShow(deleteOverlay, cancelButton);
        }

        WeakReferenceMessenger.Default.Register<ApplicationShortcutMessage>(this, (recipient, message) =>
        {
            if (message.Value != ApplicationShortcut.Find)
            {
                return;
            }

            var objectNameBox = this.FindControl<TextBox>("ObjectNameBox");
            objectNameBox?.Focus();
            objectNameBox?.SelectAll();
        });
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
