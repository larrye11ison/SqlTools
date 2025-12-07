using Caliburn.Micro;
using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

namespace SqlTools.Scripting
{
    /// <summary>
    /// A collection of DB objects that have been scripted out (the "documents").
    /// </summary>
    [Export]
    public class ScriptedObjectsViewModel : Conductor<ScriptedObjectDocumentViewModel>.Collection.OneActive,
        IHandle<FontFamily>, IHandle<ScriptedObjectDocumentViewModel>
    {
        [Import]
        private IEventAggregator eventagg = null;

        private string fontFamilyName;

        public bool CanFormat
        {
            get { return ActiveItem != null; }
        }

        [Import]
        public IShell Shell { get; set; }

        public void CloseActiveDocument()
        {
            CloseActiveTab();
        }

        public void CloseActiveTab()
        {
            if (ActiveItem == null) return;
            Items.Remove(ActiveItem);
        }

        public void FindNext()
        {
            DoItIfThereIsAnActiveItem(doc => doc.FindNext());
        }

        public void FindPrevious()
        {
            DoItIfThereIsAnActiveItem(doc => doc.FindPrevious());
        }

        public void Format()
        {
            DoItIfThereIsAnActiveItem(doc =>
            {
                var tmp = doc.FormatSql;
                doc.FormatSql = !tmp;
                doc.SetSqlFormat();
            });
        }

        public Task HandleAsync(FontFamily message, CancellationToken cancellationToken)
        {
            fontFamilyName = message.Source;
            foreach (var item in Items)
            {
                item.SetFontFamily(message);
            }
            return Task.CompletedTask;
        }

        public async Task HandleAsync(ScriptedObjectDocumentViewModel message, CancellationToken cancellationToken)
        {
            message.SetFontFamily(new FontFamily(fontFamilyName ?? "Lucida Console Regular"));
            await ActivateItemAsync(message, cancellationToken);
        }

        public void InitiateFindText()
        {
            DoItIfThereIsAnActiveItem(doc => doc.InitiateFindText());
        }

        protected override Task OnInitializedAsync(CancellationToken cancellationToken)
        {
            eventagg.SubscribeOnPublishedThread(this);
            return base.OnInitializedAsync(cancellationToken);
        }

        private void DoItIfThereIsAnActiveItem(Action<ScriptedObjectDocumentViewModel> theAction)
        {
            if (ActiveItem == null)
            {
                return;
            }
            theAction(ActiveItem);
        }
    }
}