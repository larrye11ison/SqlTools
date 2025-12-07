using LinqKit;
using System;
using System.Linq;
using System.Linq.Expressions;
using System.Text.RegularExpressions;

namespace SqlTools.Shell
{
    /// <summary>
    /// Manages the "find as you type" filtering of DB object search results from the server.
    /// </summary>
    public class DBObjectSearch
    {
        // breaks the search text into chunks
        private static readonly Regex chunker = new Regex(@"(?<chunk>[a-z_:!]+)",
            RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture | RegexOptions.Compiled);

        private static readonly StringComparison comparisonType = StringComparison.InvariantCultureIgnoreCase;

        public static Expression<Func<DBObjectViewModel, bool>> BuildPredicateFromSearchText(string filterText)
        {
            // separate out the filter by chunks - the items sep'd by whitespace
            var filterParts =
                from m in chunker.Matches(filterText).Cast<Match>()
                where m.Captures.Count > 0 && string.IsNullOrWhiteSpace(m.Captures[0].Value) == false
                select m.Captures[0].Value;

            // start out with TRUE, all pieces below will be added as AND
            var pred = PredicateBuilder.New<DBObjectViewModel>();

            foreach (var filterChunk in filterParts)
            {
                var item = filterChunk;
                var whatToEqual = true;
                // if there's a BANG at the front of this chunk, negate it
                if (item.StartsWith("!"))
                {
                    whatToEqual = false;
                    item = item.Substring(1);
                }

                if (item.Contains(':'))
                {
                    var bitch = item.Split(':');
                    var field = bitch.First().ToLower();
                    var filterString = bitch.Skip(1).First();

                    switch (field)
                    {
                        case "schema":
                        case "sch":
                            pred = pred.And(dbObject => (dbObject.SysObject.schema_name.IndexOf(filterString, comparisonType) >= 0) == whatToEqual);
                            break;

                        case "nm":
                        case "name":
                            pred = pred.And(dbObject => (dbObject.SysObject.object_name.IndexOf(filterString, comparisonType) >= 0) == whatToEqual);
                            break;

                        case "db":
                            pred = pred.And(dbObject => (dbObject.SysObject.db_name.IndexOf(filterString, comparisonType) >= 0) == whatToEqual);
                            break;

                        case "type":
                            pred = pred.And(dbObject => (dbObject.SysObject.type_desc.IndexOf(filterString, comparisonType) >= 0) == whatToEqual);
                            break;

                        default:
                            // do nothing - an unmatched "field" is ignored, not added to the predicate
                            break;
                    }
                }
                else
                {
                    pred = pred.And(dbObject => (dbObject.SearchText.IndexOf(item, comparisonType) >= 0) == whatToEqual);
                }
            }
            return pred;
        }
    }
}