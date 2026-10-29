using System.Globalization;
using MailKit;
using MailKit.Search;

namespace ProtonBridgeBridge;

public sealed class SearchParser(string label, IReadOnlyCollection<uint> processed, uint maxUid = uint.MaxValue)
{
    public SearchQuery Parse(IReadOnlyList<ImapValue> input) => Parse(input, 0, new SearchBudget());

    private SearchQuery Parse(IReadOnlyList<ImapValue> input, int depth, SearchBudget budget)
    {
        var index = 0;

        SearchQuery One(int currentDepth)
        {
            // Bound boolean recursion as well as parenthesized nesting before handing a query tree to MailKit.
            if (currentDepth > 24 || --budget.Remaining < 0 || index >= input.Count)
            {
                throw new ImapSyntaxException();
            }

            var value = input[index++];
            if (value is ListValue list)
            {
                return Parse(list.Values, currentDepth + 1, budget);
            }

            string Arg() => index < input.Count ? input[index++].Text() : throw new ImapSyntaxException();

            SearchQuery Labels()
            {
                if (Arg() != label)
                {
                    throw new ImapSyntaxException();
                }

                return processed.Count == 0 ? SearchQuery.Not(SearchQuery.All) : SearchQuery.Uids(processed.Select(id => new UniqueId(id)).ToList());
            }

            DateTime Date() => DateTime.ParseExact(Arg(), "d-MMM-yyyy", CultureInfo.InvariantCulture);

            return value.Upper() switch
            {
                "ALL" => SearchQuery.All,
                "NOT" => SearchQuery.Not(One(currentDepth + 1)),
                "OR" => SearchQuery.Or(One(currentDepth + 1), One(currentDepth + 1)),
                "X-GM-LABELS" or "KEYWORD" => Labels(),
                "UNKEYWORD" => SearchQuery.Not(Labels()),
                "UID" => SearchQuery.Uids(Syntax.Uids(Arg(), maxUid)),
                "SEEN" => SearchQuery.Seen,
                "UNSEEN" => SearchQuery.NotSeen,
                "FLAGGED" => SearchQuery.Flagged,
                "UNFLAGGED" => SearchQuery.NotFlagged,
                "ANSWERED" => SearchQuery.Answered,
                "UNANSWERED" => SearchQuery.NotAnswered,
                "DELETED" => SearchQuery.Deleted,
                "UNDELETED" => SearchQuery.NotDeleted,
                "DRAFT" => SearchQuery.Draft,
                "UNDRAFT" => SearchQuery.NotDraft,
                "RECENT" => SearchQuery.Recent,
                "OLD" => SearchQuery.NotRecent,
                "NEW" => SearchQuery.New,
                "SINCE" => SearchQuery.DeliveredAfter(Date()),
                "BEFORE" => SearchQuery.DeliveredBefore(Date()),
                "ON" => SearchQuery.DeliveredOn(Date()),
                "FROM" => SearchQuery.FromContains(Arg()),
                "TO" => SearchQuery.ToContains(Arg()),
                "CC" => SearchQuery.CcContains(Arg()),
                "BCC" => SearchQuery.BccContains(Arg()),
                "SUBJECT" => SearchQuery.SubjectContains(Arg()),
                "BODY" => SearchQuery.BodyContains(Arg()),
                "TEXT" => SearchQuery.MessageContains(Arg()),
                "HEADER" => SearchQuery.HeaderContains(Arg(), Arg()),
                _ => throw new ImapSyntaxException()
            };
        }

        if (input.Count == 0)
        {
            throw new ImapSyntaxException();
        }

        var query = One(depth);
        while (index < input.Count)
        {
            query = SearchQuery.And(query, One(depth));
        }

        return query;
    }
    private sealed class SearchBudget
    {
        public int Remaining { get; set; } = 256;
    }
}
