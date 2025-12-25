using Caliburn.Micro;
using SqlTools.Shell;
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

        protected override Task OnDeactivateAsync(bool close, CancellationToken cancellationToken)
        {
            if (close)
            {
                eventagg?.Unsubscribe(this);
            }
            return base.OnDeactivateAsync(close, cancellationToken);
        }

        protected override async Task OnInitializedAsync(CancellationToken cancellationToken)
        {
            eventagg.SubscribeOnPublishedThread(this);
            
            // TEMPORARY: Load test SQL files from local directory
            await LoadTestSqlFilesAsync();
            
            await base.OnInitializedAsync(cancellationToken);
        }

        /// <summary>
        /// TEMPORARY TEST METHOD - Loads SQL test files from a hardcoded directory
        /// TODO: REMOVE THIS METHOD when formatting testing is complete
        /// </summary>
        private async Task LoadTestSqlFilesAsync()
        {
            const string testFilesPath = @"C:\Users\pj\Documents\sql-test-files";
            
            try
            {
                if (!System.IO.Directory.Exists(testFilesPath))
                {
                    var message = new ShellMessage
                    {
                        MessageText = $"Test files directory not found: {testFilesPath}",
                        Severity = Severity.Warning
                    };
                    await eventagg.PublishOnUIThreadAsync(message);
                    return;
                }

                var files = System.IO.Directory.GetFiles(testFilesPath, "*.*", System.IO.SearchOption.TopDirectoryOnly);
                
                if (files.Length == 0)
                {
                    var message = new ShellMessage
                    {
                        MessageText = $"No files found in test directory: {testFilesPath}",
                        Severity = Severity.Warning
                    };
                    await eventagg.PublishOnUIThreadAsync(message);
                    return;
                }

                foreach (var filePath in files)
                {
                    try
                    {
                        string fileContent = await System.IO.File.ReadAllTextAsync(filePath);
                        
                        var doc = IoC.Get<ScriptedObjectDocumentViewModel>();
                        doc.InitializeFromFile(filePath, fileContent);
                        
                        Items.Add(doc);
                    }
                    catch (Exception ex)
                    {
                        var message = new ShellMessage
                        {
                            MessageText = $"Error loading test file {System.IO.Path.GetFileName(filePath)}: {ex.Message}",
                            Severity = Severity.Warning
                        };
                        await eventagg.PublishOnUIThreadAsync(message);
                    }
                }

                // Activate the first item if any were loaded
                if (Items.Count > 0)
                {
                    await ActivateItemAsync(Items[0], default);
                }

                var successMessage = new ShellMessage
                {
                    MessageText = $"Loaded {Items.Count} test SQL file(s) from {testFilesPath}",
                    Severity = Severity.Info
                };
                await eventagg.PublishOnUIThreadAsync(successMessage);
            }
            catch (Exception ex)
            {
                var message = new ShellMessage
                {
                    MessageText = $"Error accessing test files directory: {ex.Message}",
                    Severity = Severity.Warning
                };
                await eventagg.PublishOnUIThreadAsync(message);
            }
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