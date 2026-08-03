using DataSync.LHYY.V2.Models.Dto;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// 根据卡片父级和关联表单关系，计算已配置子卡之间的最近父级。
/// </summary>
public static class SubCardHierarchyHelper
{
    public static Dictionary<Guid, Guid?> BuildMappedParentMap(
        IEnumerable<Guid> mappedCardIds,
        IReadOnlyDictionary<Guid, CardInfo>? cards)
    {
        var mappedIds = mappedCardIds.ToHashSet();
        var result = mappedIds.ToDictionary(id => id, _ => (Guid?)null);
        if (cards == null || cards.Count == 0)
        {
            return result;
        }

        var parentMap = BuildParentMap(cards);
        foreach (var cardId in mappedIds)
        {
            result[cardId] = FindNearestMappedParent(cardId, mappedIds, parentMap);
        }

        return result;
    }

    private static Dictionary<Guid, List<Guid>> BuildParentMap(IReadOnlyDictionary<Guid, CardInfo> cards)
    {
        var result = new Dictionary<Guid, List<Guid>>();
        foreach (var card in cards.Values)
        {
            if (card.ParentId.HasValue)
            {
                AddParent(result, card.Id, card.ParentId.Value);
            }
        }

        foreach (var referenceCard in cards.Values.Where(card => card.RelatedFormId.HasValue))
        {
            var relatedFormId = referenceCard.RelatedFormId!.Value;
            var relatedRoots = cards.Values.Where(card =>
                card.FormId == relatedFormId
                && (!card.ParentId.HasValue
                    || !cards.TryGetValue(card.ParentId.Value, out var parent)
                    || parent.FormId != relatedFormId));

            foreach (var root in relatedRoots)
            {
                AddParent(result, root.Id, referenceCard.Id);
            }
        }

        return result;
    }

    private static void AddParent(Dictionary<Guid, List<Guid>> parentMap, Guid cardId, Guid parentId)
    {
        if (!parentMap.TryGetValue(cardId, out var parents))
        {
            parents = [];
            parentMap[cardId] = parents;
        }

        if (!parents.Contains(parentId))
        {
            parents.Add(parentId);
        }
    }

    private static Guid? FindNearestMappedParent(
        Guid cardId,
        HashSet<Guid> mappedIds,
        IReadOnlyDictionary<Guid, List<Guid>> parentMap)
    {
        if (!parentMap.TryGetValue(cardId, out var initialParents))
        {
            return null;
        }

        var visited = new HashSet<Guid> { cardId };
        var currentLevel = initialParents.Distinct().OrderBy(id => id).ToList();
        while (currentLevel.Count > 0)
        {
            var mappedParent = currentLevel.FirstOrDefault(mappedIds.Contains);
            if (mappedParent != Guid.Empty)
            {
                return mappedParent;
            }

            var nextLevel = new List<Guid>();
            foreach (var current in currentLevel)
            {
                if (!visited.Add(current) || !parentMap.TryGetValue(current, out var parents))
                {
                    continue;
                }

                nextLevel.AddRange(parents.Where(parent => !visited.Contains(parent)));
            }

            currentLevel = nextLevel.Distinct().OrderBy(id => id).ToList();
        }

        return null;
    }
}
