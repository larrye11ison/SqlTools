using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Controls;
using Dock.Model.Core;
using SqlPhanos.Docking;
using SqlPhanos.Enums;
using SqlPhanos.Messages;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace SqlPhanos.ViewModels;

public partial class ShellViewModel : ObservableObject, IRecipient<OpenDocumentMessage>
{
    [ObservableProperty]
    private IFactory? _factory;

    [ObservableProperty]
    private IRootDock? _layout;

    [ObservableProperty]
    private string _statusMessage = "Loading...";

    public ShellViewModel()
    {
        Debug.WriteLine("ShellViewModel constructor called");

        _factory = new DockFactory(this);
        Layout = _factory.CreateLayout();
        if (Layout is not null)
        {
            _factory.InitLayout(Layout);
        }

        WeakReferenceMessenger.Default.Register(this);
    }

    public void Receive(OpenDocumentMessage message)
    {
        AddDocument(message.Value);
    }

    private void AddDocument(SqlDocumentViewModel document)
    {
        if (Layout is null || Factory is null) return;

        var documentDock = FindDocumentDock(Layout);
        if (documentDock is not null)
        {
            Factory.AddDockable(documentDock, document);
            Factory.SetActiveDockable(document);
            Factory.SetFocusedDockable(documentDock, document);
        }
    }

    [RelayCommand]
    private void ExecuteShortcut(ApplicationShortcut shortcut)
    {
        // Handle document navigation locally in ShellViewModel
        if (shortcut == ApplicationShortcut.NextDocument || shortcut == ApplicationShortcut.PreviousDocument)
        {
            NavigateDocuments(shortcut == ApplicationShortcut.NextDocument);
            return;
        }

        // IFactory doesn't expose FocusedDockable/ActiveDockable directly in the interface.
        // We need to look at the Layout (IRootDock) to find the active content.
        // IRootDock has ActiveDockable.

        IDockable? context = null;
        if (Layout is IRootDock root)
        {
            context = root.ActiveDockable;

            // If the active dockable is a dock (like a ProportionalDock), we might need to drill down?
            // But usually ActiveDockable on the Root is the active content or the active container.
            // Let's try to find the focused one if possible, but Dock.Model doesn't seem to track focus globally on the Factory interface easily.
            // However, the DockControl binds FocusedDockable to the Factory? No.

            // Let's assume Layout.ActiveDockable is the high-level active item.
            // If we want the leaf, we might need to traverse.
            // For now, let's send what we have.
        }

        Debug.WriteLine($"Executing shortcut: {shortcut} Context: {context?.Title ?? "None"}");
        WeakReferenceMessenger.Default.Send(new ApplicationShortcutMessage(shortcut, context));
    }

    private void NavigateDocuments(bool next)
    {
        if (Layout is null || Factory is null) return;

        var documentDock = FindDocumentDock(Layout);
        if (documentDock is not null && documentDock.VisibleDockables?.Count > 1)
        {
            var index = -1;
            if (documentDock.ActiveDockable != null)
            {
                index = documentDock.VisibleDockables.IndexOf(documentDock.ActiveDockable);
            }

            if (index == -1)
            {
                if (documentDock.VisibleDockables.Count > 0)
                {
                    Factory.SetActiveDockable(documentDock.VisibleDockables[0]);
                }
                return;
            }

            int newIndex;
            if (next)
            {
                newIndex = (index + 1) % documentDock.VisibleDockables.Count;
            }
            else
            {
                newIndex = (index - 1 + documentDock.VisibleDockables.Count) % documentDock.VisibleDockables.Count;
            }

            Factory.SetActiveDockable(documentDock.VisibleDockables[newIndex]);
        }
    }

    private IDocumentDock? FindDocumentDock(IDockable dockable)
    {
        if (dockable is IDocumentDock documentDock && documentDock.Id == "Documents")
        {
            return documentDock;
        }

        if (dockable is IDock dock)
        {
            if (dock.VisibleDockables is not null)
            {
                foreach (var visible in dock.VisibleDockables)
                {
                    var result = FindDocumentDock(visible);
                    if (result is not null) return result;
                }
            }
        }

        return null;
    }
}