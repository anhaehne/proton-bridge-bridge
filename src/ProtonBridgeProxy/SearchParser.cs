using System.Globalization;
using MailKit;
using MailKit.Search;

namespace ProtonBridgeProxy;

public sealed class SearchParser(string label, IReadOnlyCollection<uint> processed, uint maxUid = uint.MaxValue)
{
    public SearchQuery Parse(IReadOnlyList<ImapValue> input)
    {
        int index = 0;
        SearchQuery One()
        {
            if (index >= input.Count) throw new ImapSyntaxException();
            var value = input[index++];
            if (value is ListValue list) return Parse(list.Values);
            string Arg() => index < input.Count ? input[index++].Text() : throw new ImapSyntaxException();
            SearchQuery Labels()
            {
                if (Arg() != label) throw new ImapSyntaxException();
                return processed.Count == 0 ? SearchQuery.Not(SearchQuery.All) : SearchQuery.Uids(processed.Select(id => new UniqueId(id)).ToList());
            }
            DateTime Date() => DateTime.ParseExact(Arg(), "d-MMM-yyyy", CultureInfo.InvariantCulture);
            return value.Upper() switch
            {
                "ALL" => SearchQuery.All,
                "NOT" => SearchQuery.Not(One()),
                "OR" => SearchQuery.Or(One(), One()),
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
        if (input.Count == 0) throw new ImapSyntaxException();
        var query = One();
        while (index < input.Count) query = SearchQuery.And(query, One());
        return query;
    }
}
