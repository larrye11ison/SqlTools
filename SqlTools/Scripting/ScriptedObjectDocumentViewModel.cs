using Caliburn.Micro;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using SqlTools.Shell;
using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Reactive.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml;

namespace SqlTools.Scripting
{
    /// <summary>
    /// The script for one object from the database. Shown in a tabbed UI in the app interface.
    /// </summary>
    [Export]
    [PartCreationPolicy(CreationPolicy.NonShared)]
    public class ScriptedObjectDocumentViewModel : Screen, IDocument//, IHandle<FontFamily>
    {
        private SearchResultsHighlight _curentResultsHighlight;

        private SearchResultsHighlight _generalHighlight;

        private string _originalSqlText;

        private TextEditor editor;

        [Import]
        private IEventAggregator eventagg = null;

        private TextBox findTextBox;

        // TODO: get rid of the disposables when closing the document
        private IDisposable findTextChangedSubscription;

        private IDisposable sqlTextPropChangedSub;

        public ScriptedObjectDocumentViewModel()
        {
            IsLoadingDefinition = true;
            FindText = new FindTextViewModel("");
        }

        private enum TextSearchDirection
        {
            Forwards,
            Backwards
        }

        public string CanonicalName { get; set; }

        public FindTextViewModel FindText { get; set; }

        public bool FormatSql { get; set; }

        public System.Uri IconUri
        {
            get
            {
                var iconPath = "/Media/AppIcon.ico";
                switch (TypeDescription)
                {
                    case "AGGREGATE_FUNCTION":
                    case "SQL_TABLE_VALUED_FUNCTION":
                    case "SQL_SCALAR_FUNCTION":
                    case "CLR_SCALAR_FUNCTION":
                    case "SQL_INLINE_TABLE_VALUED_FUNCTION":
                        iconPath = "/Media/func-scalar.png";
                        break;

                    case "CLR_STORED_PROCEDURE":
                    case "SQL_STORED_PROCEDURE":
                    case "EXTENDED_STORED_PROCEDURE":
                        iconPath = "/Media/storedprocedure.png";
                        break;

                    case "PRIMARY_KEY_CONSTRAINT":
                    case "DEFAULT_CONSTRAINT":
                    case "UNIQUE_CONSTRAINT":
                    case "CHECK_CONSTRAINT":
                    case "FOREIGN_KEY_CONSTRAINT":
                        break;

                    case "INTERNAL_TABLE":
                    case "SYSTEM_TABLE":
                    case "USER_TABLE":
                        iconPath = "/Media/table.png";
                        break;

                    case "SERVICE_QUEUE":
                        break;

                    case "SQL_TRIGGER":
                        iconPath = "/Media/trigger.png";
                        break;

                    case "VIEW":
                        iconPath = "/Media/view.png";
                        break;
                }
                var rv = new System.Uri(iconPath, System.UriKind.Relative);
                return rv;
            }
        }

        public bool IsLoadingDefinition { get; set; }

        public string SqlText { get; set; }

        public string Title { get { return DisplayName; } }

        public string TypeDescription { get; set; }

        public void EditorLoaded()
        {
            var type = typeof(ScriptedObjectDocumentViewModel);
            var fullName = type.Namespace + ".tsql.xshd";

            IHighlightingDefinition hl;
            using (var stream = type.Assembly.GetManifestResourceStream(fullName))
            {
                using (var reader = new XmlTextReader(stream))
                {
                    hl = HighlightingLoader.Load(reader, HighlightingManager.Instance);
                }
            }

            this.editor.SyntaxHighlighting = hl;

            // AvalonEdit is NOT able to bind its Text property, so we have to JAM it in there...
            editor.Text = SqlText;
            SqlText = _originalSqlText;
            FindText = new FindTextViewModel(SqlText);

            // set up the highlighting for search results
            _generalHighlight = new SearchResultsHighlight("general");
            _curentResultsHighlight = new SearchResultsHighlight("current")
            {
                ForegroundBrush = Brushes.Black,
                BackgroundBrush = Brushes.Orange,
            };

            editor.TextArea.TextView.LineTransformers.Add(_generalHighlight);
            editor.TextArea.TextView.LineTransformers.Add(_curentResultsHighlight);

            editor.TextArea.TextView.Redraw();
        }

        public void FindNext()
        {
            if (!FindText.HasResults()) return;
            FindText.NextResult();
            _curentResultsHighlight.FindResults = new FoundTextResult[] { FindText.ActiveResult };
            editor.ScrollToLine(editor.Document.GetLineByOffset(FindText.ActiveResult.StartOffset).LineNumber);
            editor.TextArea.TextView.Redraw();
        }

        public void FindPrevious()
        {
            if (!FindText.HasResults()) return;
            FindText.PreviousResult();
            _curentResultsHighlight.FindResults = new FoundTextResult[] { FindText.ActiveResult };
            editor.ScrollToLine(editor.Document.GetLineByOffset(FindText.ActiveResult.StartOffset).LineNumber);
            editor.TextArea.TextView.Redraw();
        }

        public override int GetHashCode()
        {
            return ToString().GetHashCode();
        }

        //public void Handle(FontFamily message)
        //{
        //    editor.FontFamily = message;
        //}

        public void Initialize(ScriptedObjectInfo info)
        {
            SqlText = info.ObjectDefinition;
            _originalSqlText = info.ObjectDefinition;
            TypeDescription = info.DbObject.type_desc;
            DisplayName = info.DbObject.full_name;
            CanonicalName = info.DbObject.LongDescription;
            NotifyOfPropertyChange(() => Title);
        }

        public void InitiateFindText()
        {
            findTextBox.Focus();
            findTextBox.SelectAll();
        }

        public void SetSqlFormat()
        {
            if (editor == null || SqlText == null)
            {
                return;
            }
            string textToUse = null;
            if (FormatSql)
            {
                textToUse = PoorMansTSqlFormatterLib.SqlFormattingManager.DefaultFormat(_originalSqlText);
            }
            else
            {
                textToUse = _originalSqlText;
            }
            SqlText = textToUse;
            editor.Text = SqlText;
        }

        public void TextSearch(string c)
        {
            System.Diagnostics.Debug.WriteLine("Searching for: " + c);

            FindText.SearchText = c;

            _generalHighlight.FindResults = FindText.Results;
            _curentResultsHighlight.FindResults = new FoundTextResult[] { FindText.ActiveResult };

            if (FindText.ActiveResult != null)
            {
                editor.ScrollToLine(editor.Document.GetLineByOffset(FindText.ActiveResult.StartOffset).LineNumber);
            }

            editor.TextArea.TextView.Redraw();
        }

        public void ToggleSqlFormat()
        {
            var searchy = FindText.SearchText;
            TextSearch("");
            FormatSql = !FormatSql;
            TextSearch(searchy);
        }

        public override string ToString()
        {
            return CanonicalName;
        }

        public override void TryClose(bool? dialogResult = null)
        {
            //base.TryClose(dialogResult);
            if (findTextChangedSubscription != null)
            {
                findTextChangedSubscription.Dispose();
            }
            if (sqlTextPropChangedSub != null)
            {
                sqlTextPropChangedSub.Dispose();
            }
        }

        private FontFamily font = null;

        internal void SetFontFamily(FontFamily message)
        {
            font = message;
            if (this.editor != null)
            {
                editor.FontFamily = message;
            }
        }

        protected override void OnViewLoaded(object viewObject)
        {
            base.OnViewLoaded(viewObject);
            var view = viewObject as ScriptedObjectDocumentView;
            if (view == null)
            {
                return;
            }
            editor = view.editor;
            EditorLoaded();
            editor.FontFamily = font ?? new FontFamily("Consolas");
            findTextBox = view.findTextBox;
            findTextChangedSubscription = Observable
                .FromEventPattern<TextChangedEventArgs>(findTextBox, "TextChanged")
                .Select(c => ((TextBox)c.Sender).Text)
                .DistinctUntilChanged()
                .Throttle(TimeSpan.FromMilliseconds(200))
                .ObserveOnDispatcher()
                .Subscribe(c => TextSearch(c));

            Observable.FromEventPattern<PropertyChangedEventArgs>(this, "PropertyChanged")
                .Where(pc => pc.EventArgs.PropertyName == "FormatSql")
                .SubscribeOnDispatcher()
                .Subscribe(b => SetSqlFormat());

            sqlTextPropChangedSub = Observable.FromEventPattern<PropertyChangedEventArgs>(this, "PropertyChanged")
                .Where(pc => pc.EventArgs.PropertyName == "SqlText")
                .SubscribeOnDispatcher()
                .Subscribe(b => FindText = new FindTextViewModel(SqlText));
            eventagg.Subscribe(this);
        }
    }
}