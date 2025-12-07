using Caliburn.Micro;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using SqlTools.Shell;
using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace SqlTools.Scripting
{
    /// <summary>
    /// The script for one object from the database. Shown in a tabbed UI in the app interface.
    /// </summary>
    [Export]
    [PartCreationPolicy(CreationPolicy.NonShared)]
    public class ScriptedObjectDocumentViewModel : Screen, IDocument //, IHandle<FontFamily>
    {
        private SearchResultsHighlight _currentResultsHighlight;
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

        private enum TextSearchDirection { Forwards, Backwards }

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

                    case "INTERNAL_TABLE":
                    case "SYSTEM_TABLE":
                    case "USER_TABLE":
                        iconPath = "/Media/table.png";
                        break;

                    case "SQL_TRIGGER":
                        iconPath = "/Media/trigger.png";
                        break;

                    case "VIEW":
                        iconPath = "/Media/view.png";
                        break;
                }
                return new System.Uri(iconPath, System.UriKind.Relative);
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

            IHighlightingDefinition hl = null;
            try
            {
                using (var stream = type.Assembly.GetManifestResourceStream(fullName))
                {
                    if (stream != null)
                    using (var reader = new XmlTextReader(stream))
                    {
                        hl = HighlightingLoader.Load(reader, HighlightingManager.Instance);
                    }
                }
            }
            catch
            {
                // ignore highlighting errors
            }

            if (hl != null && editor != null)
                editor.SyntaxHighlighting = hl;

            // AvalonEdit cannot bind Text directly
            if (editor != null) editor.Text = SqlText;
            FindText = new FindTextViewModel(SqlText ?? "");

            _generalHighlight = new SearchResultsHighlight("general");
            _currentResultsHighlight = new SearchResultsHighlight("current")
            {
                ForegroundBrush = Brushes.Black,
                BackgroundBrush = Brushes.Orange
            };

            if (editor != null)
            {
                editor.TextArea.TextView.LineTransformers.Add(_generalHighlight);
                editor.TextArea.TextView.LineTransformers.Add(_currentResultsHighlight);
                editor.TextArea.TextView.Redraw();
            }
        }

        public void FindNext()
        {
            if (!FindText.HasResults()) return;
            FindText.NextResult();
            _currentResultsHighlight.FindResults = new FoundTextResult[] { FindText.ActiveResult };
            if (editor != null && FindText.ActiveResult != null)
                editor.ScrollToLine(editor.Document.GetLineByOffset(FindText.ActiveResult.StartOffset).LineNumber);
            if (editor != null) editor.TextArea.TextView.Redraw();
        }

        public void FindPrevious()
        {
            if (!FindText.HasResults()) return;
            FindText.PreviousResult();
            _currentResultsHighlight.FindResults = new FoundTextResult[] { FindText.ActiveResult };
            if (editor != null && FindText.ActiveResult != null)
                editor.ScrollToLine(editor.Document.GetLineByOffset(FindText.ActiveResult.StartOffset).LineNumber);
            if (editor != null) editor.TextArea.TextView.Redraw();
        }

        public override int GetHashCode()
        {
            return ToString().GetHashCode();
        }

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
            findTextBox?.Focus();
            findTextBox?.SelectAll();
        }

        public void SetSqlFormat()
        {
            if (editor == null || SqlText == null) return;

            string textToUse;
            if (FormatSql)
            {
                try
                {
                    textToUse = CanonicalFormatSql(_originalSqlText);
                }
                catch (Exception ex)
                {
                    // fall back and log
                    textToUse = _originalSqlText;
                    Debug.WriteLine("SQL formatting error: " + ex.Message);
                }
            }
            else
            {
                textToUse = _originalSqlText;
            }

            SqlText = textToUse;
            if (editor != null) editor.Text = SqlText;
        }

        private string CanonicalFormatSql(string sql)
        {
            if (string.IsNullOrWhiteSpace(sql)) return sql;

            var parser = new TSql160Parser(false);
            IList<ParseError> errors;
            TSqlFragment fragment;
            using (var reader = new StringReader(sql))
            {
                fragment = parser.Parse(reader, out errors);
            }
            if (errors != null && errors.Count > 0)
                throw new Exception($"SQL Parsing Error: {errors[0].Message}");

            var options = new SqlScriptGeneratorOptions
            {
                KeywordCasing = KeywordCasing.Uppercase,
                IndentationSize = 4,
                IncludeSemicolons = true
            };

            var generator = new Sql150ScriptGenerator(options);
            generator.GenerateScript(fragment, out string formattedSql);
            if (string.IsNullOrEmpty(formattedSql)) return sql;
            return formattedSql.Replace("\r\n", Environment.NewLine);
        }

        public void TextSearch(string c)
        {
            Debug.WriteLine("Searching for: " + c);

            FindText.SearchText = c;

            _generalHighlight.FindResults = FindText.Results;
            _currentResultsHighlight.FindResults = new FoundTextResult[] { FindText.ActiveResult };

            if (FindText.ActiveResult != null && editor != null)
                editor.ScrollToLine(editor.Document.GetLineByOffset(FindText.ActiveResult.StartOffset).LineNumber);

            if (editor != null) editor.TextArea.TextView.Redraw();
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
            findTextChangedSubscription?.Dispose();
            sqlTextPropChangedSub?.Dispose();
        }

        private FontFamily font = null;

        internal void SetFontFamily(FontFamily message)
        {
            font = message;
            if (this.editor != null) editor.FontFamily = message;
        }

        protected override void OnViewLoaded(object viewObject)
        {
            base.OnViewLoaded(viewObject);
            var view = viewObject as ScriptedObjectDocumentView;
            if (view == null) return;
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
                .Subscribe(_ => SetSqlFormat());

            sqlTextPropChangedSub = Observable.FromEventPattern<PropertyChangedEventArgs>(this, "PropertyChanged")
                .Where(pc => pc.EventArgs.PropertyName == "SqlText")
                .SubscribeOnDispatcher()
                .Subscribe(_ => FindText = new FindTextViewModel(SqlText));

            eventagg?.Subscribe(this);
        }
    }
}