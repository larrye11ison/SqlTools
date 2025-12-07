using Caliburn.Micro;
using SqlTools.ObjectSearch;
using SqlTools.Scripting;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;

namespace SqlTools.Shell
{
    [Export(typeof(IShell))]
    public class ShellViewModel : Conductor<IScreen>.Collection.AllActive,
        IShell, IHandle<ScriptedObjectDocumentViewModel>,
        IHandle<ShellMessage>
    {
        public ScriptedObjectDocumentViewModel ActiveTab { get; set; }

        public bool CanAddNewConnection
        {
            get { return ObjectSearchVisible; }
        }

        public bool CanCloseCurrentDocument
        {
            get { return CanExecuteDocumentAction(); }
        }

        public bool CanFindNext
        {
            get { return CanExecuteDocumentAction(); }
        }

        public bool CanFindPrevious
        {
            get { return CanExecuteDocumentAction(); }
        }

        public bool CanFindText
        {
            get { return CanExecuteDocumentAction(); }
        }

        public bool CanToggleSqlFormatOnCurrentDocument
        {
            get { return CanExecuteDocumentAction(); }
        }

        [Import]
        public IEventAggregator EventAggregator { get; set; }

        public ObjectSearchViewModel ObjectSearch { get; set; }

        public bool ObjectSearchVisible { get; set; }

        public ScriptedObjectsViewModel ScriptedObjects { get; set; }

        public void ActivateDocument(ScriptedObjectDocumentViewModel scriptedObject)
        {
            _ = ScriptedObjects.ActivateItemAsync(scriptedObject, CancellationToken.None);
        }

        public void AddNewConnection()
        {
            ObjectSearch.Connections.AddNewConnection();
            if (!ObjectSearchVisible)
            {
                ObjectSearchVisible = true;
            }
        }

        public bool CanExecuteDocumentAction()
        {
            return true;
            // TODO: why is this short circuited? I don't even remember...
            //return ScriptedObjects.ActiveItem != null
            //    && !ObjectSearchVisible;
        }

        public void ChangeSQLFont()
        {
            // FontChooser now handles loading the current font itself
            var fontChooser = new Settings.FontChooser(loadCurrentFont: true);

            if (fontChooser.ShowDialog() == true)
            {
                _ = EventAggregator.PublishOnUIThreadAsync(fontChooser.SelectedFontFamily);
            }
        }

        public void CloseCurrentDocument()
        {
            ScriptedObjects.CloseActiveTab();
            if (ScriptedObjects.Items.Count == 0)
            {
                ObjectSearchVisible = true;
            }
        }

        public void CycleVisibility()
        {
            var t = ObjectSearchVisible;
            ObjectSearchVisible = !t;
        }

        public void FindNext()
        {
            ScriptedObjects.FindNext();
        }

        public void FindPrevious()
        {
            ScriptedObjects.FindPrevious();
        }

        public void FindText()
        {
            if (ObjectSearchVisible)
            {
                ObjectSearchVisible = false;
            }
            ScriptedObjects.InitiateFindText();
        }

        public void GoToServerSearch()
        {
            ObjectSearchVisible = true;
            this.ObjectSearch.InitializeNewObjectSearchForActiveDatabaseConnection();
        }

        public Task HandleAsync(ShellMessage message, CancellationToken cancellationToken)
        {
            string format = string.Format("{0}: {1}", message.Severity, message.MessageText);
            System.Diagnostics.Debug.WriteLine(format);
            return Task.CompletedTask;
        }

        public Task HandleAsync(ScriptedObjectDocumentViewModel message, CancellationToken cancellationToken)
        {
            ObjectSearchVisible = false;
            return Task.CompletedTask;
        }

        public void OpenDocument(ScriptedObjectDocumentViewModel scriptedObject)
        {
            ScriptedObjects.Items.Add(scriptedObject);
            _ = ScriptedObjects.ActivateItemAsync(scriptedObject, CancellationToken.None);
            if (ObjectSearchVisible)
            {
                ObjectSearchVisible = false;
            }
        }

        public void ToggleSqlFormatOnCurrentDocument()
        {
            ScriptedObjects.ActiveItem.ToggleSqlFormat();
        }

        protected override async Task OnInitializedAsync(CancellationToken cancellationToken)
        {
            EventAggregator.SubscribeOnPublishedThread(this);

            DisplayName = "SQL Tools";
            ObjectSearchVisible = true;

            // set up the child viewmodels
            ScriptedObjects = IoC.Get<ScriptedObjectsViewModel>();
            ObjectSearch = IoC.Get<ObjectSearchViewModel>();
            await ActivateItemAsync(ScriptedObjects, cancellationToken);
            await ActivateItemAsync(ObjectSearch, cancellationToken);

            await base.OnInitializedAsync(cancellationToken);
        }
    }
}