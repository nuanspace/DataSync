using DataSync.LHYY.V2.Models.Dto;
using DataSync.LHYY.V2.Models.Entities;
using DataSync.LHYY.V2.Models.Enums;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// 整条消息映射预览服务：只读执行提取链路，不写入业务库。
/// </summary>
public class MessageMappingPreviewService
{
    private readonly ConfigService _configService;
    private readonly FieldMappingExecutor _mappingExecutor;
    private readonly FilterRuleService _filterRuleService;

    public MessageMappingPreviewService(
        ConfigService configService,
        FieldMappingExecutor mappingExecutor,
        FilterRuleService filterRuleService)
    {
        _configService = configService;
        _mappingExecutor = mappingExecutor;
        _filterRuleService = filterRuleService;
    }

    public async Task<MessageMappingPreviewResult> PreviewAsync(EsbMessage message)
    {
        var result = new MessageMappingPreviewResult
        {
            MessageId = message.Id,
            TranCode = message.TranCode,
            TranName = message.TranName
        };

        var config = await _configService.GetInterfaceConfigAsync(message.TranCode, message.IntegrationProjectCode);
        if (config == null)
        {
            result.ErrorMessage = $"未找到接口配置：{message.TranCode}";
            return result;
        }

        result.InterfaceName = config.TranName;
        result.HandlerType = config.HandlerType;
        if (config.HandlerType is not (HandlerType.Generic or HandlerType.GenericQuestionWriteBack))
        {
            result.Warnings.Add($"当前接口处理器为 {config.HandlerType}，预览仅展示配置映射提取结果，不能代表自定义处理器的全部逻辑。");
        }

        if (!MessageJsonHelper.TryParseToken(message.RawJson, out var root, out var error))
        {
            result.ErrorMessage = error ?? "Raw JSON 解析失败";
            return result;
        }

        if (!TryBuildPayloadSlices(root, config, out var slices, out var sliceError))
        {
            result.ErrorMessage = sliceError;
            return result;
        }

        if (slices.Count > 1)
        {
            result.Warnings.Add($"已按主记录数组 {config.MainRecordArrayPath} 拆成 {slices.Count} 条记录预览。");
            var filteredCount = 0;
            foreach (var slice in slices)
            {
                var sliceResult = await PreviewPayloadAsync(message, config, slice.Payload);
                if (sliceResult.IsFiltered)
                {
                    filteredCount++;
                    AddFilteredSliceRow(result, slice.RecordIndex, sliceResult.FilterReason);
                    continue;
                }

                MergeSliceResult(result, sliceResult, slice.RecordIndex);
            }

            if (filteredCount == slices.Count)
            {
                result.IsFiltered = true;
                result.FilterReason = "所有主记录均被接口级过滤。";
            }

            result.Sections = result.Sections.Where(section => section.Rows.Count > 0).ToList();
            if (result.Sections.Count == 0)
            {
                result.Warnings.Add("当前消息按现有配置未提取到任何映射结果。");
            }

            return result;
        }

        var singleResult = await PreviewPayloadAsync(message, config, slices[0].Payload);
        result.IsFiltered = singleResult.IsFiltered;
        result.FilterReason = singleResult.FilterReason;
        result.Warnings.AddRange(singleResult.Warnings);
        result.Sections = singleResult.Sections;
        return result;
    }

    private async Task<MessageMappingPreviewResult> PreviewPayloadAsync(
        EsbMessage message,
        EsbInterfaceConfig config,
        JToken root)
    {
        var result = new MessageMappingPreviewResult
        {
            MessageId = message.Id,
            TranCode = message.TranCode,
            TranName = message.TranName,
            InterfaceName = config.TranName,
            HandlerType = config.HandlerType
        };

        var mainContext = MessageJsonHelper.ResolveMainRecordContext(root, config.MainRecordArrayPath);
        result.Sections.Add(BuildContextSection(root, mainContext, config));

        var interfaceFilterResult = await _filterRuleService.ApplyInterfaceFiltersAsync(
            root,
            message.TranCode,
            config.IntegrationProjectCode,
            config.MainRecordArrayPath);
        if (!interfaceFilterResult.IsPassed)
        {
            result.IsFiltered = true;
            result.FilterReason = interfaceFilterResult.Reason;
            return result;
        }

        var patientMappings = await _mappingExecutor.LoadMappingsAsync(message.TranCode, MappingTarget.Patient, config.IntegrationProjectCode);
        var eventMappings = await _mappingExecutor.LoadMappingsAsync(message.TranCode, MappingTarget.Event, config.IntegrationProjectCode);
        var questionMappings = await _mappingExecutor.LoadMappingsAsync(message.TranCode, MappingTarget.Question, config.IntegrationProjectCode);
        var subCardMappings = await _mappingExecutor.LoadMappingsAsync(message.TranCode, MappingTarget.SubCard, config.IntegrationProjectCode);

        var patientFields = await _mappingExecutor.ExtractPatientFieldsAsync(
            root,
            message.TranCode,
            config.IntegrationProjectCode,
            config.MainRecordArrayPath);
        result.Sections.Add(BuildDictionarySection("Patient 字段", patientFields, patientMappings));

        var eventFields = await _mappingExecutor.ExtractEventFieldsAsync(
            root,
            message.TranCode,
            config.IntegrationProjectCode,
            config.MainRecordArrayPath);
        result.Sections.Add(BuildDictionarySection("Event 字段", eventFields, eventMappings));

        var questionValues = await _mappingExecutor.ExtractQuestionValuesAsync(
            root,
            message.TranCode,
            config.IntegrationProjectCode,
            config.MainRecordArrayPath);
        result.Sections.Add(BuildQuestionSection("题目答案", questionValues, questionMappings));

        var subCardData = await _mappingExecutor.ExtractSubCardDataAsync(
            root,
            message.TranCode,
            interfaceFilterResult.RowFilterResults,
            config.IntegrationProjectCode,
            config.MainRecordArrayPath);
        result.Sections.Add(BuildSubCardSection(subCardData, subCardMappings));

        result.Sections = result.Sections.Where(section => section.Rows.Count > 0).ToList();
        if (result.Sections.Count == 0)
        {
            result.Warnings.Add("当前消息按现有配置未提取到任何映射结果。");
        }

        return result;
    }

    private static MessageMappingPreviewSection BuildContextSection(
        JToken root,
        JToken mainContext,
        EsbInterfaceConfig config)
    {
        var section = new MessageMappingPreviewSection { Name = "接口上下文" };
        AddContextRow(section, "病案号", "MrnSourcePath", MessageJsonHelper.ReadString(root, config.MrnSourcePath, mainContext));
        AddContextRow(section, "住院次数", "VisitNoSourcePath", MessageJsonHelper.ReadString(root, config.VisitNoSourcePath, mainContext));
        AddContextRow(section, "就诊号/住院号", "InpatientNoSourcePath", MessageJsonHelper.ReadString(root, config.InpatientNoSourcePath, mainContext));
        AddContextRow(section, "事件开始时间", "EventStartTimeSourcePath", MessageJsonHelper.ReadDateTime(root, config.EventStartTimeSourcePath, mainContext)?.ToString("yyyy-MM-dd HH:mm:ss"));
        return section;
    }

    private static void AddContextRow(MessageMappingPreviewSection section, string target, string pathName, string? value)
    {
        section.Rows.Add(new MessageMappingPreviewRow
        {
            Target = target,
            Value = value,
            Note = pathName,
            IsWarning = string.IsNullOrWhiteSpace(value)
        });
    }

    private static MessageMappingPreviewSection BuildDictionarySection(
        string name,
        Dictionary<string, string?> values,
        List<EsbFieldMapping> mappings)
    {
        var mappingByTarget = mappings
            .GroupBy(m => m.TargetField, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var section = new MessageMappingPreviewSection { Name = name };
        foreach (var (target, value) in values.OrderBy(v => v.Key))
        {
            mappingByTarget.TryGetValue(target, out var mapping);
            section.Rows.Add(new MessageMappingPreviewRow
            {
                Target = target,
                DisplayName = mapping?.Description,
                Value = value,
                Note = BuildMappingNote(mapping),
                IsWarning = string.IsNullOrEmpty(value)
            });
        }

        return section;
    }

    private static MessageMappingPreviewSection BuildQuestionSection(
        string name,
        List<QuestionValue> values,
        List<EsbFieldMapping> mappings)
    {
        var labelByQuestionId = BuildQuestionLabelMap(mappings);
        var section = new MessageMappingPreviewSection { Name = name };
        foreach (var item in values)
        {
            var key = item.QuestionId.ToString();
            labelByQuestionId.TryGetValue(key, out var label);
            section.Rows.Add(new MessageMappingPreviewRow
            {
                Target = key,
                DisplayName = label.DisplayName,
                Value = item.Value?.ToString(),
                Note = item.IsDictMiss ? "字典未命中，选择题正式写入时会跳过" : label.Note,
                IsWarning = item.IsDictMiss
            });
        }

        return section;
    }

    private static MessageMappingPreviewSection BuildSubCardSection(
        List<SubCardData> subCardData,
        List<EsbFieldMapping> mappings)
    {
        var labelByQuestionId = BuildQuestionLabelMap(mappings);
        var section = new MessageMappingPreviewSection { Name = "SubCard 子卡片" };
        foreach (var card in subCardData)
        {
            for (var rowIndex = 0; rowIndex < card.Rows.Count; rowIndex++)
            {
                foreach (var item in card.Rows[rowIndex])
                {
                    var key = item.QuestionId.ToString();
                    labelByQuestionId.TryGetValue(key, out var label);
                    section.Rows.Add(new MessageMappingPreviewRow
                    {
                        GroupName = BuildSubCardGroupName(card.CardId, rowIndex),
                        Target = key,
                        DisplayName = label.DisplayName,
                        Value = item.Value?.ToString(),
                        Note = item.IsDictMiss
                            ? $"CardId={card.CardId}，第 {rowIndex + 1} 行，字典未命中，选择题正式写入时会跳过"
                            : $"CardId={card.CardId}，第 {rowIndex + 1} 行{FormatNoteSuffix(label.Note)}",
                        IsWarning = item.IsDictMiss
                    });
                }
            }
        }

        return section;
    }

    private static Dictionary<string, (string? DisplayName, string? Note)> BuildQuestionLabelMap(List<EsbFieldMapping> mappings)
    {
        return mappings
            .Where(m => !EsbFieldMapping.IsSubCardFilterMapping(m))
            .GroupBy(m => m.TargetField, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var mapping = g.First();
                    return (mapping.Description, BuildMappingNote(mapping));
                },
                StringComparer.OrdinalIgnoreCase);
    }

    private static string? BuildMappingNote(EsbFieldMapping? mapping)
    {
        if (mapping == null)
            return null;

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(mapping.SourcePath))
            parts.Add($"源路径：{mapping.SourcePath}");
        if (!string.IsNullOrWhiteSpace(mapping.DictCode))
            parts.Add($"字典：{mapping.DictCode}");
        if (!string.IsNullOrWhiteSpace(mapping.ValueExpression))
            parts.Add($"表达式：{mapping.ValueExpression}");

        return parts.Count == 0 ? null : string.Join("；", parts);
    }

    private static string FormatNoteSuffix(string? note) =>
        string.IsNullOrWhiteSpace(note) ? "" : $"，{note}";

    private static void MergeSliceResult(
        MessageMappingPreviewResult target,
        MessageMappingPreviewResult source,
        string? recordIndex)
    {
        target.Warnings.AddRange(source.Warnings);

        foreach (var sourceSection in source.Sections)
        {
            var targetSection = target.Sections.FirstOrDefault(section => section.Name == sourceSection.Name);
            if (targetSection == null)
            {
                targetSection = new MessageMappingPreviewSection { Name = sourceSection.Name };
                target.Sections.Add(targetSection);
            }

            foreach (var row in sourceSection.Rows)
            {
                targetSection.Rows.Add(new MessageMappingPreviewRow
                {
                    GroupName = BuildRecordGroupName(recordIndex, row.GroupName),
                    Target = row.Target,
                    DisplayName = row.DisplayName,
                    Value = row.Value,
                    Note = BuildRecordNote(recordIndex, row.Note),
                    IsWarning = row.IsWarning
                });
            }
        }
    }

    private static void AddFilteredSliceRow(
        MessageMappingPreviewResult result,
        string? recordIndex,
        string? reason)
    {
        var section = result.Sections.FirstOrDefault(item => item.Name == "过滤结果");
        if (section == null)
        {
            section = new MessageMappingPreviewSection { Name = "过滤结果" };
            result.Sections.Add(section);
        }

        section.Rows.Add(new MessageMappingPreviewRow
        {
            Target = "接口级过滤",
            Value = "未通过",
            Note = BuildRecordNote(recordIndex, reason),
            IsWarning = true
        });
    }

    private static string? BuildRecordNote(string? recordIndex, string? note)
    {
        var prefix = string.IsNullOrWhiteSpace(recordIndex) ? "" : $"记录 {recordIndex}";
        if (string.IsNullOrWhiteSpace(prefix))
            return note;

        return string.IsNullOrWhiteSpace(note) ? prefix : $"{prefix}；{note}";
    }

    private static string BuildSubCardGroupName(Guid cardId, int rowIndex) =>
        $"CardId={cardId}，第 {rowIndex + 1} 行";

    private static string? BuildRecordGroupName(string? recordIndex, string? groupName)
    {
        if (string.IsNullOrWhiteSpace(groupName))
            return null;

        return string.IsNullOrWhiteSpace(recordIndex)
            ? groupName
            : $"记录 {recordIndex}，{groupName}";
    }

    private static bool TryBuildPayloadSlices(
        JToken root,
        EsbInterfaceConfig config,
        out List<PreviewPayloadSlice> slices,
        out string? error)
    {
        slices = [];
        error = null;

        if (string.IsNullOrWhiteSpace(config.MainRecordArrayPath))
        {
            slices.Add(new PreviewPayloadSlice(root, null));
            return true;
        }

        var arrayPath = SubCardPathHelper.NormalizeArrayContainerPath(config.MainRecordArrayPath);
        if (string.IsNullOrWhiteSpace(arrayPath))
        {
            error = "主记录数组路径为空";
            return false;
        }

        var arrayToken = MessageJsonHelper.SafeSelectToken(root, arrayPath);
        if (arrayToken is not JArray array)
        {
            error = $"主记录数组路径未命中数组：{arrayPath}";
            return false;
        }

        if (array.Count == 0)
        {
            error = $"主记录数组为空：{arrayPath}";
            return false;
        }

        for (var index = 0; index < array.Count; index++)
        {
            if (!TryBuildProjectedPayload(root, arrayPath, array[index], out var projectedPayload))
            {
                error = $"主记录数组拆分失败：{arrayPath}[{index}]";
                return false;
            }

            slices.Add(new PreviewPayloadSlice(projectedPayload, index.ToString()));
        }

        return true;
    }

    private static bool TryBuildProjectedPayload(JToken root, string arrayPath, JToken item, out JToken projectedPayload)
    {
        if (string.IsNullOrWhiteSpace(arrayPath) || SubCardPathHelper.IsRootContainerPath(arrayPath))
        {
            projectedPayload = new JArray(item.DeepClone());
            return true;
        }

        if (TryBuildSimpleProjectedPayload(root, arrayPath, item, out projectedPayload))
            return true;

        projectedPayload = root.DeepClone();
        var targetToken = MessageJsonHelper.SafeSelectToken(projectedPayload, arrayPath);
        if (targetToken == null)
            return false;

        targetToken.Replace(new JArray(item.DeepClone()));
        return true;
    }

    private static bool TryBuildSimpleProjectedPayload(
        JToken root,
        string arrayPath,
        JToken item,
        out JToken projectedPayload)
    {
        projectedPayload = null!;
        if (root is not JObject rootObject ||
            !arrayPath.StartsWith("$.", StringComparison.Ordinal) ||
            arrayPath.Contains('[', StringComparison.Ordinal) ||
            arrayPath.Contains(']', StringComparison.Ordinal))
        {
            return false;
        }

        var segments = arrayPath[2..].Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return false;

        if (!TryCloneObjectWithArrayItem(rootObject, segments, 0, item, out var clonedRoot))
            return false;

        projectedPayload = clonedRoot;
        return true;
    }

    private static bool TryCloneObjectWithArrayItem(
        JObject source,
        string[] segments,
        int depth,
        JToken item,
        out JObject cloned)
    {
        cloned = [];
        var targetName = segments[depth];
        var found = false;

        foreach (var property in source.Properties())
        {
            if (!string.Equals(property.Name, targetName, StringComparison.Ordinal))
            {
                cloned.Add(property.Name, property.Value.DeepClone());
                continue;
            }

            found = true;
            if (depth == segments.Length - 1)
            {
                if (property.Value is not JArray)
                    return false;

                cloned.Add(property.Name, new JArray(item.DeepClone()));
                continue;
            }

            if (property.Value is not JObject child ||
                !TryCloneObjectWithArrayItem(child, segments, depth + 1, item, out var clonedChild))
            {
                return false;
            }

            cloned.Add(property.Name, clonedChild);
        }

        return found;
    }

    private sealed record PreviewPayloadSlice(JToken Payload, string? RecordIndex);
}
