using DataSync.LHYY.V2.Models.Dto;
using System.Collections;
using System.Reflection;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// FormBrowser 静态辅助方法
/// </summary>
public static class FormBrowserHelper
{
    /// <summary>
    /// 从 form_question 对象构建 QuestionInfo
    /// </summary>
    public static QuestionInfo BuildQuestionInfo(object q)
    {
        var id = GetPropertyValue(q, "id") ?? "";
        var title = GetPropertyValue(q, "display_name") ?? GetPropertyValue(q, "label_text") ?? id;
        var labelText = GetPropertyValue(q, "label_text");
        var promptText = GetPropertyValue(q, "prompt_text");
        var prefixText = GetPropertyValue(q, "prefix_text");
        var suffixText = GetPropertyValue(q, "suffix_text");
        var dimensionText = GetPropertyValue(q, "dimension_text");
        var tableName = GetPropertyValue(q, "table_name");
        var columnName = GetPropertyValue(q, "column_name");
        var dataType = GetPropertyValue(q, "data_type") ?? "";
        var cardIdStr = GetPropertyValue(q, "card_id") ?? "";
        var formIdStr = GetPropertyValue(q, "form_id") ?? "";
        var formName = GetPropertyValue(q, "form_name") ?? "未分类";
        var sortIndexStr = GetPropertyValue(q, "sort_index");
        int.TryParse(sortIndexStr, out var sortIndex);

        // 选择题信息
        string? selectInfo = null;
        var parsedOptions = new List<string>();
        if (dataType == "选择")
        {
            var isMultiple = GetPropertyRawValue(q, "select_is_multiple_choice");
            var isMultipleBool = isMultiple is true;
            var selectType = isMultipleBool ? "多选" : "单选";

            var options = GetPropertyRawValue(q, "select_sorted_option_subset");
            if (options is IEnumerable<object> optList)
            {
                parsedOptions = optList.Select(o => o.ToString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
                selectInfo = $"{selectType}: {string.Join(" / ", parsedOptions)}";
            }
            else if (options is IEnumerable<string> optStrList)
            {
                parsedOptions = optStrList.Where(s => !string.IsNullOrEmpty(s)).ToList();
                selectInfo = $"{selectType}: {string.Join(" / ", parsedOptions)}";
            }
            else
            {
                selectInfo = selectType;
            }
        }

        Guid.TryParse(cardIdStr, out var cardGuid);
        Guid? formId = Guid.TryParse(formIdStr, out var formGuid) ? formGuid : null;
        var preUidStr = GetPropertyValue(q, "pre_uid");
        Guid? preUid = Guid.TryParse(preUidStr, out var preGuid) ? preGuid : null;

        return new QuestionInfo
        {
            Id = id,
            Title = title,
            LabelText = labelText,
            PromptText = promptText,
            PrefixText = prefixText,
            SuffixText = suffixText,
            DimensionText = dimensionText,
            TableName = tableName,
            ColumnName = columnName,
            DataType = dataType,
            CardGuid = cardGuid,
            FormId = formId,
            FormName = formName,
            SortIndex = sortIndex,
            PreUid = preUid,
            SelectInfo = selectInfo,
            Options = parsedOptions,
        };
    }

    /// <summary>
    /// 安全获取属性/字段值
    /// </summary>
    public static string? GetPropertyValue(object obj, string name)
    {
        var type = obj.GetType();
        var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop != null)
        {
            var val = prop.GetValue(obj);
            return val?.ToString();
        }
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (field != null)
        {
            var val = field.GetValue(obj);
            return val?.ToString();
        }
        return null;
    }

    /// <summary>
    /// 安全获取属性原始值
    /// </summary>
    public static object? GetPropertyRawValue(object obj, string name)
    {
        var type = obj.GetType();
        var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop != null) return prop.GetValue(obj);
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (field != null) return field.GetValue(obj);
        return null;
    }

    /// <summary>
    /// 构建 Form > Card > Question 树
    /// </summary>
    public static List<FormNode> BuildTree(List<QuestionInfo> questions, Dictionary<Guid, CardInfo> cardDict) =>
        BuildTree(questions, cardDict, null);

    /// <summary>
    /// 构建 Form > Card > Question 树，并优先使用 form_form.sort_index 排序
    /// </summary>
    public static List<FormNode> BuildTree(
        List<QuestionInfo> questions,
        Dictionary<Guid, CardInfo> cardDict,
        IReadOnlyDictionary<Guid, FormInfo>? formDict)
    {
        var formGroups = questions
            .GroupBy(q => new
            {
                q.FormId,
                FormName = q.FormName ?? "未分类",
            })
            .Select(g =>
            {
                var fallbackName = g.Key.FormName;
                var formInfo = g.Key.FormId.HasValue && formDict != null && formDict.TryGetValue(g.Key.FormId.Value, out var info)
                    ? info
                    : null;

                return new
                {
                    FormId = g.Key.FormId,
                    FormName = formInfo?.Name ?? fallbackName,
                    SortIndex = formInfo?.SortIndex ?? int.MaxValue,
                    Questions = g.ToList(),
                };
            })
            .OrderBy(g => g.SortIndex)
            .ThenBy(g => g.FormName, StringComparer.OrdinalIgnoreCase);

        var result = new List<FormNode>();

        foreach (var formGroup in formGroups)
        {
            var formNode = new FormNode
            {
                Id = formGroup.FormId?.ToString() ?? formGroup.FormName,
                Name = formGroup.FormName,
                SortIndex = formGroup.SortIndex,
                IsExpanded = true,
            };

            var withCard = formGroup.Questions.Where(q => q.CardGuid != Guid.Empty).GroupBy(q => q.CardGuid);
            var withoutCard = formGroup.Questions.Where(q => q.CardGuid == Guid.Empty).ToList();

            var cardNodes = new Dictionary<Guid, CardNode>();

            foreach (var cardInfo in cardDict.Values.Where(card =>
                         CardBelongsToForm(card, formGroup.FormId, formGroup.FormName)))
            {
                GetOrCreateCardNode(cardInfo.Id, cardInfo, cardNodes, cardDict);
            }

            foreach (var cardGroup in withCard)
            {
                var cardId = cardGroup.Key;
                var cardInfo = cardDict.GetValueOrDefault(cardId);
                var cardNode = GetOrCreateCardNode(cardId, cardInfo, cardNodes, cardDict);
                cardNode.Questions.AddRange(cardGroup);
            }

            foreach (var cn in cardNodes.Values)
            {
                var parentId = cardDict.GetValueOrDefault(cn.CardId)?.ParentId;
                if (parentId == null || !cardNodes.ContainsKey(parentId.Value))
                {
                    formNode.Cards.Add(cn);
                }
            }

            // 链表排序：卡片和问题
            formNode.Cards = SortByLinkedList(formNode.Cards, c => c.CardId, c => c.PreUid);
            formNode.OrphanQuestions = SortByLinkedList(withoutCard, q => Guid.Parse(q.Id), q => q.PreUid);
            SortCardChildrenRecursive(formNode.Cards);
            formNode.QuestionCount = formGroup.Questions.Count;

            result.Add(formNode);
        }

        return ExpandRelatedForms(result, cardDict, formDict);
    }

    /// <summary>
    /// 获取 Form 根节点下按链表顺序混排后的子节点
    /// </summary>
    public static List<FormTreeChildNode> GetOrderedChildren(FormNode form) =>
        BuildOrderedChildren(form.Cards, form.OrphanQuestions);

    /// <summary>
    /// 获取 Card 节点下按链表顺序混排后的子节点（SubCard / Question）
    /// </summary>
    public static List<FormTreeChildNode> GetOrderedChildren(CardNode card) =>
        BuildOrderedChildren(card.SubCards, card.Questions);

    /// <summary>
    /// 递归排序卡片的子卡片和问题（链表排序）
    /// </summary>
    private static void SortCardChildrenRecursive(List<CardNode> cards)
    {
        foreach (var card in cards)
        {
            card.Questions = SortByLinkedList(card.Questions, q => Guid.Parse(q.Id), q => q.PreUid);
            var sorted = SortByLinkedList(card.SubCards, c => c.CardId, c => c.PreUid);
            card.SubCards.Clear();
            card.SubCards.AddRange(sorted);
            SortCardChildrenRecursive(card.SubCards);
        }
    }

    /// <summary>
    /// 按 pre_uid 链表排序：找到链表头（pre_uid 为空或指向不在集合中的节点），沿链遍历
    /// </summary>
    public static List<T> SortByLinkedList<T>(IEnumerable<T> items, Func<T, Guid> getId, Func<T, Guid?> getPreUid)
    {
        var list = items.ToList();
        if (list.Count <= 1) return list;

        var idSet = new HashSet<Guid>(list.Select(getId));
        var byPre = new Dictionary<Guid, List<T>>();
        var heads = new List<T>();

        foreach (var item in list)
        {
            var pre = getPreUid(item);
            if (pre == null || !idSet.Contains(pre.Value))
            {
                heads.Add(item);
            }
            else
            {
                if (!byPre.TryGetValue(pre.Value, out var bucket))
                {
                    bucket = [];
                    byPre[pre.Value] = bucket;
                }
                bucket.Add(item);
            }
        }

        var result = new List<T>(list.Count);
        var visited = new HashSet<Guid>();

        foreach (var head in heads)
        {
            var current = head;
            while (current != null)
            {
                var currentId = getId(current);
                if (!visited.Add(currentId)) break;
                result.Add(current);
                current = byPre.TryGetValue(currentId, out var nexts)
                    ? nexts.FirstOrDefault(n => !visited.Contains(getId(n)))
                    : default;
            }
        }

        // 未被链表覆盖的节点追加到末尾
        foreach (var item in list)
        {
            if (!visited.Contains(getId(item)))
                result.Add(item);
        }

        return result;
    }

    private static List<FormTreeChildNode> BuildOrderedChildren(IEnumerable<CardNode> cards, IEnumerable<QuestionInfo> questions)
    {
        var children = cards
            .Select(card => new FormTreeChildNode { Card = card })
            .Concat(questions.Select(question => new FormTreeChildNode { Question = question }))
            .ToList();

        return SortByLinkedList(
            children,
            child => child.Card?.CardId ?? Guid.Parse(child.Question!.Id),
            child => child.Card?.PreUid ?? child.Question?.PreUid);
    }

    private static CardNode GetOrCreateCardNode(Guid cardId, CardInfo? cardInfo,
        Dictionary<Guid, CardNode> cardNodes, Dictionary<Guid, CardInfo> cardDict)
    {
        if (cardNodes.TryGetValue(cardId, out var existing))
            return existing;

        var node = new CardNode
        {
            CardId = cardId,
            Name = cardInfo?.Name ?? cardId.ToString(),
            CardType = cardInfo?.CardType ?? "default",
            FormId = cardInfo?.FormId,
            RelatedFormId = cardInfo?.RelatedFormId,
            PreUid = cardInfo?.PreUid,
        };
        cardNodes[cardId] = node;

        if (cardInfo?.ParentId != null)
        {
            var parentInfo = cardDict.GetValueOrDefault(cardInfo.ParentId.Value);
            var parentNode = GetOrCreateCardNode(cardInfo.ParentId.Value, parentInfo, cardNodes, cardDict);
            parentNode.SubCards.Add(node);
        }

        return node;
    }

    private static bool CardBelongsToForm(CardInfo card, Guid? formId, string formName)
    {
        if (formId.HasValue)
        {
            return card.FormId == formId;
        }

        return !card.FormId.HasValue
               && card.FormName.Equals(formName, StringComparison.OrdinalIgnoreCase);
    }

    private static List<FormNode> ExpandRelatedForms(
        List<FormNode> forms,
        IReadOnlyDictionary<Guid, CardInfo> cardDict,
        IReadOnlyDictionary<Guid, FormInfo>? formDict)
    {
        var sourceForms = forms
            .Where(form => Guid.TryParse(form.Id, out _))
            .ToDictionary(
                form => Guid.Parse(form.Id),
                form => new FormNode
                {
                    Id = form.Id,
                    Name = form.Name,
                    Cards = form.Cards.Select(CloneCardNode).ToList(),
                    OrphanQuestions = form.OrphanQuestions.ToList(),
                });
        var referencedFormIds = cardDict.Values
            .Where(card => card.RelatedFormId.HasValue)
            .Select(card => card.RelatedFormId!.Value)
            .ToHashSet();

        foreach (var form in forms)
        {
            var formPath = Guid.TryParse(form.Id, out var currentFormId)
                ? new HashSet<Guid> { currentFormId }
                : [];
            foreach (var card in form.Cards)
            {
                ExpandRelatedCards(card, sourceForms, new HashSet<Guid>(formPath));
            }

            AssignParentSubCards(form.Cards, null);
            form.QuestionCount = CountQuestions(form.Cards) + form.OrphanQuestions.Count;
        }

        return forms
            .Where(form => !Guid.TryParse(form.Id, out var formId)
                           || !referencedFormIds.Contains(formId)
                           || formDict?.GetValueOrDefault(formId)?.IsHidden != true)
            .ToList();
    }

    private static void ExpandRelatedCards(
        CardNode card,
        IReadOnlyDictionary<Guid, FormNode> sourceForms,
        HashSet<Guid> formPath)
    {
        foreach (var child in card.SubCards.ToList())
        {
            ExpandRelatedCards(child, sourceForms, new HashSet<Guid>(formPath));
        }

        if (!card.RelatedFormId.HasValue
            || formPath.Contains(card.RelatedFormId.Value)
            || !sourceForms.TryGetValue(card.RelatedFormId.Value, out var relatedForm))
        {
            return;
        }

        var relatedPath = new HashSet<Guid>(formPath) { card.RelatedFormId.Value };
        card.Questions.AddRange(relatedForm.OrphanQuestions);
        foreach (var relatedRoot in relatedForm.Cards)
        {
            var clone = CloneCardNode(relatedRoot);
            ExpandRelatedCards(clone, sourceForms, new HashSet<Guid>(relatedPath));
            card.SubCards.Add(clone);
        }

        SortCardChildrenRecursive([card]);
    }

    private static CardNode CloneCardNode(CardNode source) => new()
    {
        CardId = source.CardId,
        Name = source.Name,
        CardType = source.CardType,
        FormId = source.FormId,
        RelatedFormId = source.RelatedFormId,
        PreUid = source.PreUid,
        IsExpanded = source.IsExpanded,
        Questions = source.Questions.ToList(),
        SubCards = source.SubCards.Select(CloneCardNode).ToList(),
    };

    private static void AssignParentSubCards(IEnumerable<CardNode> cards, CardNode? parentSubCard)
    {
        foreach (var card in cards)
        {
            card.ParentSubCardId = parentSubCard?.CardId;
            card.ParentSubCardName = parentSubCard?.Name;
            var currentSubCard = card.CardType is "multiple" or "table" ? card : parentSubCard;
            AssignParentSubCards(card.SubCards, currentSubCard);
        }
    }

    private static int CountQuestions(IEnumerable<CardNode> cards) =>
        cards.Sum(card => card.Questions.Count + CountQuestions(card.SubCards));
}
