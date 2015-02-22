using System;
using System.Collections.ObjectModel;
using System.Diagnostics.Contracts;
using System.Linq;

namespace SqlTools.Shell
{
    /// <summary>
    /// Used to manage finding text within a specific scripted object's source text.
    /// </summary>
    public class FindTextViewModel : Caliburn.Micro.PropertyChangedBase
    {
        private readonly ObservableCollection<FoundTextResult> _results = new ObservableCollection<FoundTextResult>();
        private int _activeResultIndex = -1;
        private string _searchText;

        public FindTextViewModel(string scriptText)
        {
            ScriptText = scriptText;
        }

        public FoundTextResult ActiveResult
        {
            get
            {
                if (!HasResults())
                {
                    return null;
                }
                return _results.OrderBy(r => r.Index).Skip(_activeResultIndex).First();
            }
        }

        public int? ActiveResultIndex
        {
            get
            {
                if (_results.Count == 0)
                {
                    return null;
                }
                return _activeResultIndex;
            }
        }

        public string Description
        {
            get
            {
                if (HasResults())
                {
                    return string.Format("{0} of {1}", ActiveResultIndex + 1, NumberOfResults);
                }
                return "No matches.";
            }
        }

        public int? NumberOfResults
        {
            get
            {
                if (_results.Count == 0)
                {
                    return null;
                }
                return _results.Count;
            }
        }

        public ObservableCollection<FoundTextResult> Results
        {
            get
            {
                return _results;
            }
        }

        public string ScriptText { get; private set; }

        public string SearchText
        {
            get
            {
                return _searchText;
            }
            set
            {
                if (_searchText != value)
                {
                    _searchText = value;
                    Query();
                }
            }
        }

        public bool HasResults()
        {
            return Results.Count > 0;
        }

        public void NextResult()
        {
            var maxItCanBe = Results.Count - 1;
            _activeResultIndex++;
            if (_activeResultIndex > maxItCanBe)
            {
                _activeResultIndex = 0;
            }
            FIRE();
        }

        public void PreviousResult()
        {
            _activeResultIndex--;
            if (_activeResultIndex < 0)
            {
                _activeResultIndex = Results.Count - 1;
            }
            FIRE();
        }

        private void FIRE()
        {
            NotifyOfPropertyChange(() => NumberOfResults);
            NotifyOfPropertyChange(() => ActiveResult);
            NotifyOfPropertyChange(() => ActiveResultIndex);
            NotifyOfPropertyChange(() => Description);
        }

        private void Query()
        {
            Contract.Requires(Results != null);
            Contract.Ensures(Results.Any(ftr => ftr == null) == false);

            Results.Clear();
            if (ScriptText == null || string.IsNullOrEmpty(SearchText))
            {
                FIRE();
                return;
            }

            int res = -1;
            int index = 0;
            while ((res = ScriptText.IndexOf(SearchText, res + 1, StringComparison.CurrentCultureIgnoreCase)) >= 0)
            {
                Results.Add(new FoundTextResult(index++) { StartOffset = res, EndOffset = res + SearchText.Length });
            }

            _activeResultIndex = 0;
            FIRE();
        }
    }
}