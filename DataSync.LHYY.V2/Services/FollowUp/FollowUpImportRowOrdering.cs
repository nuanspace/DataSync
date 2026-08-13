using System.Text.Json;

namespace DataSync.LHYY.V2.Services.FollowUp;

internal static class FollowUpImportRowOrdering
{
    internal static bool RequiresOrdering(string schema, string table) =>
        schema.Equals("form", StringComparison.OrdinalIgnoreCase)
        && table.Equals("form_card", StringComparison.OrdinalIgnoreCase);

    internal static IReadOnlyList<string> Order(
        string schema,
        string table,
        IEnumerable<string> rows)
    {
        var materialized = rows.Where(row => !string.IsNullOrWhiteSpace(row)).ToList();
        if (!RequiresOrdering(schema, table) || materialized.Count < 2)
            return materialized;

        var nodes = materialized.Select(ParseCard).ToList();
        var packageIds = nodes.Select(node => node.Id).ToHashSet();
        var emittedIds = new HashSet<Guid>();
        var ordered = new List<string>(nodes.Count);

        while (ordered.Count < nodes.Count)
        {
            var progressed = false;
            foreach (var node in nodes)
            {
                if (node.Emitted
                    || node.ParentId.HasValue
                    && packageIds.Contains(node.ParentId.Value)
                    && !emittedIds.Contains(node.ParentId.Value))
                    continue;

                node.Emitted = true;
                emittedIds.Add(node.Id);
                ordered.Add(node.Row);
                progressed = true;
            }

            if (!progressed)
                throw new InvalidDataException("form.form_card 数据包含循环父子关系，不能安全导入。");
        }

        return ordered;
    }

    private static CardRow ParseCard(string row)
    {
        using var document = JsonDocument.Parse(row);
        var root = document.RootElement;
        if (!root.TryGetProperty("id", out var idValue)
            || idValue.ValueKind != JsonValueKind.String
            || !idValue.TryGetGuid(out var id))
            throw new InvalidDataException("form.form_card 数据缺少有效的 uuid 主键 id。");

        Guid? parentId = null;
        if (root.TryGetProperty("parent_id", out var parentValue)
            && parentValue.ValueKind is not JsonValueKind.Null)
        {
            if (parentValue.ValueKind != JsonValueKind.String || !parentValue.TryGetGuid(out var parsedParentId))
                throw new InvalidDataException("form.form_card 数据包含无效的 uuid parent_id。");
            parentId = parsedParentId;
        }

        return new CardRow(row, id, parentId);
    }

    private sealed class CardRow(string row, Guid id, Guid? parentId)
    {
        public string Row { get; } = row;
        public Guid Id { get; } = id;
        public Guid? ParentId { get; } = parentId;
        public bool Emitted { get; set; }
    }
}
