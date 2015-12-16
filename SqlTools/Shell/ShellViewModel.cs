using Caliburn.Micro;
using SqlTools.ObjectSearch;
using SqlTools.Scripting;
using System.ComponentModel.Composition;

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
            ScriptedObjects.ActivateItem(scriptedObject);
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
            var turd = new Settings.FontChooser();
            var res = turd.ShowDialog();
            if (turd.DialogResult.HasValue && turd.DialogResult.Value)
            {
                EventAggregator.PublishOnCurrentThread(turd.SelectedFontFamily);
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

        public void Handle(ShellMessage message)
        {
            string format = string.Format("{0}: {1}", message.Severity, message.MessageText);
            System.Diagnostics.Debug.WriteLine(format);
        }

        public void Handle(ScriptedObjectDocumentViewModel message)
        {
            ObjectSearchVisible = false;
        }

        public void OpenDocument(ScriptedObjectDocumentViewModel scriptedObject)
        {
            ScriptedObjects.Items.Add(scriptedObject);
            ScriptedObjects.ActivateItem(scriptedObject);
            if (ObjectSearchVisible)
            {
                ObjectSearchVisible = false;
            }
        }

        public void ToggleSqlFormatOnCurrentDocument()
        {
            ScriptedObjects.ActiveItem.ToggleSqlFormat();
        }

        protected override void OnInitialize()
        {
            base.OnInitialize();

            EventAggregator.Subscribe(this);

            DisplayName = "SQL Tools";
            ObjectSearchVisible = true;

            // set up the child viewmodels
            ScriptedObjects = IoC.Get<ScriptedObjectsViewModel>();
            ObjectSearch = IoC.Get<ObjectSearchViewModel>();
            ActivateItem(ScriptedObjects);
            ActivateItem(ObjectSearch);
        }
    }
}