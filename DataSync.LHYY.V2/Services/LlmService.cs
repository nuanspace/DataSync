using DataSync.LHYY.V2.Models.Dto;
using DataSync.LHYY.V2.Models.Options;
using DataSync.LHYY.V2.Models.Enums;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// LLM 调用封装（兼容 Ollama OpenAI API）
/// </summary>
public class LlmService
{
    private const int MaxJsonDepth = 5;
    private const int MaxArraySampleCount = 3;
    private const int MaxTypedArrayHintCount = 8;
    private const int MaxLeafHintDepth = 12;
    private const int MaxLeafHintCount = 120;
    private const int MaxGeneralPromptCandidateHintTargetCount = 12;
    private const int GeneralCandidateHintCountPerTarget = 12;
    private const int MaxGeneralCandidateHintCountPerTarget = 20;
    private const int FocusedCandidateCountPerTarget = 5;
    private const int MaxFocusedCandidateCountPerTarget = 8;
    private static readonly Regex TokenRegex = new(@"[\p{IsCJKUnifiedIdeographs}]+|[A-Za-z0-9]+", RegexOptions.Compiled);
    private static readonly string[][] TokenSynonymGroups =
    [
        ["icu", "重症", "重症室", "监护室", "重症监护", "重症监护室"],
        ["admission", "admit", "enter", "entry", "in", "入", "进入", "进"],
        ["discharge", "exit", "out", "leave", "出", "退出", "离开"],
        ["stay", "days", "day", "duration", "length", "时长", "停留", "住院", "天数"],
        ["total", "总"],
        ["time", "date", "时间", "时点"]
    ];
    private static readonly Dictionary<string, string[]> TokenSynonymMap = BuildTokenSynonymMap();
    private static readonly string[] TypePropertyCandidates =
    [
        "IDType",
        "IdentifierType",
        "Type",
        "Code",
        "Category",
        "Kind"
    ];
    private static readonly string[] ValuePropertyCandidates =
    [
        "IDNumber",
        "IdentifierNumber",
        "IdentifierNo",
        "Identifier",
        "Number",
        "No",
        "Value",
        "CardNo"
    ];

    private readonly HttpClient _httpClient;
    private readonly LlmOptions _defaultOptions;
    private readonly ConfigService _configService;
    private readonly ILogger<LlmService> _logger;

    public LlmService(HttpClient httpClient, IOptions<LlmOptions> options, ConfigService configService, ILogger<LlmService> logger)
    {
        _httpClient = httpClient;
        _defaultOptions = options.Value;
        _configService = configService;
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _logger = logger;
    }

    /// <summary>
    /// LLM 是否可用
    /// </summary>
    public bool IsAvailable => !string.IsNullOrWhiteSpace(_defaultOptions.BaseUrl);

    public async Task<bool> IsAvailableAsync()
    {
        var options = await GetEffectiveOptionsAsync();
        return !string.IsNullOrWhiteSpace(options.BaseUrl);
    }

    /// <summary>
    /// 批量匹配（带原始返回文本，供调试）
    /// </summary>
    public async Task<(List<MappingSuggestion> Suggestions, string RawResponse)> SuggestMappingsWithRawAsync(
        JToken sampleJson, List<TargetFieldInfo> targetFields, string? scopeHint, CancellationToken ct, string? mainRecordArrayPath = null)
    {
        var (analysisToken, sampleNote) = PrepareSingleRecordAnalysisToken(sampleJson);
        var initialResult = await SuggestMappingsWithGeneralPromptAsync(sampleJson, analysisToken, sampleNote, targetFields, scopeHint, ct, mainRecordArrayPath);
        var suggestions = initialResult.Suggestions;
        var responseText = initialResult.RawResponse;

        if (suggestions.Count == 0 && ShouldRunFocusedRetry(targetFields))
        {
            var retryResult = await SuggestMappingsWithFocusedPromptAsync(sampleJson, analysisToken, targetFields, scopeHint, ct, mainRecordArrayPath);
            if (retryResult.Suggestions.Count > 0)
            {
                suggestions = retryResult.Suggestions;
                responseText = retryResult.RawResponse;
            }
            else if (!string.Equals(responseText, retryResult.RawResponse, StringComparison.Ordinal))
            {
                responseText = $"首次返回:\n{responseText}\n\n二次重试返回:\n{retryResult.RawResponse}";
            }
        }

        if (ShouldRunFocusedRetry(targetFields) && CountCoveredTargets(suggestions) < targetFields.Count)
        {
            var completionResult = await CompleteMissingTargetSuggestionsAsync(
                sampleJson,
                analysisToken,
                sampleNote,
                targetFields,
                scopeHint,
                suggestions,
                ct,
                mainRecordArrayPath);

            if (CountCoveredTargets(completionResult.Suggestions) > CountCoveredTargets(suggestions))
            {
                suggestions = completionResult.Suggestions;
            }
        }

        // 按目标字段分组，每组只标记置信度最高的为 IsBest
        foreach (var group in suggestions.GroupBy(s => $"{s.MappingTarget}:{s.TargetField}"))
        {
            var best = group.OrderByDescending(s => s.Confidence).First();
            best.IsBest = true;
        }

        _logger.LogInformation("LLM 解析结果: {Count} 条建议", suggestions.Count);
        if (suggestions.Count == 0)
            _logger.LogWarning("LLM 完整返回内容: {Response}", responseText);
        return (suggestions, responseText);
    }

    /// <summary>
    /// 批量匹配：输入源 JSON + 目标字段列表，返回映射建议
    /// </summary>
    public async Task<List<MappingSuggestion>> SuggestMappingsAsync(
        JToken sampleJson, List<TargetFieldInfo> targetFields, string? scopeHint, CancellationToken ct, string? mainRecordArrayPath = null)
    {
        var (suggestions, _) = await SuggestMappingsWithRawAsync(sampleJson, targetFields, scopeHint, ct, mainRecordArrayPath);
        return suggestions;
    }

    private async Task<(List<MappingSuggestion> Suggestions, string RawResponse)> SuggestMappingsWithGeneralPromptAsync(
        JToken sampleJson,
        JToken analysisToken,
        string? sampleNote,
        List<TargetFieldInfo> targetFields,
        string? scopeHint,
        CancellationToken ct,
        string? mainRecordArrayPath = null)
    {
        var jsonStructure = BuildJsonStructure(analysisToken, "", 0);
        var typedArrayHintText = BuildTypedArrayFilterHintText(analysisToken);
        var candidateHintText = BuildGeneralPromptCandidateHintText(analysisToken, targetFields);
        var leafHintText = BuildLeafHintText(analysisToken);
        var scopeRuleText = BuildScopeRuleText(targetFields, scopeHint);
        var mainRecordScopeText = BuildMainRecordScopePromptText(mainRecordArrayPath);
        _logger.LogInformation("LLM 请求 - JSON 结构长度: {Len}, 目标字段数: {Count}", jsonStructure.Length, targetFields.Count);

        var targetFieldsText = string.Join("\n", targetFields.Select(FormatTargetFieldForPrompt));

        var systemPrompt = """
            你是医疗数据集成专家，负责将医院 HIS/LIS/EMR 系统的 JSON 数据映射到目标字段。

            ## 映射规则
            1. 根据字段名称、示例值、数据类型综合判断匹配关系
            2. 中文字段名和英文字段名都要考虑（如"姓名"对应 name，"性别"对应 gender）
            3. 字段名匹配前先做拆词，但拆词只用于辅助理解与候选召回；最终判断仍要结合字段整体语义、上下文和数据类型，不要机械地只按词项重合数量决定
            4. 常见医疗缩写：MRN=病历号，DOB=出生日期，IDCard/SID=身份证号
            5. 普通字段输出路径必须与源 JSON 的真实根路径保持一致；如果样本根路径包含 Request.Body，则输出时必须保留 Request.Body 前缀
            6. 嵌套路径用点号分隔（如 PatientInfo.Name）
            7. 普通字段如果需要从数组中定位具体元素，可以使用 JSONPath 过滤表达式 [?(...)]，不要盲目输出固定下标 [0]
            8. 当数组元素需要通过类型字段/标识字段区分含义时，必须优先使用过滤表达式。例如 PatientIdentifierList 中如果 IDType 决定 IDNumber 的语义，应输出类似 PatientIdentifierList[?(@.IDType=='MedicalRecordNo')].IDNumber
            9. 如果样本中已经给出了“数组过滤候选路径”，应优先从这些候选路径中挑选与目标字段语义最匹配的路径
            10. 不要仅因字段值像编号，就把证件号、卡号、手机号等路径映射到任意业务字段；只有当字段名、类型标识、上下文语义都匹配时才推荐
            11. 日期时间类字段优先匹配 Event 类型的 event_start_time / event_end_time
            12. 如果源值是编码（如"1"代表"男"），建议使用字典转换，dictCode 填写建议名称（如 gender_dict）
            13. 尽可能多匹配，即使置信度较低也输出（confidence >= 0.3 即可）
            14. 一个源字段可以匹配多个目标字段，一个目标字段也可以有多个候选源字段
            15. SubCard 匹配规则：目标字段中带有 cardId 和 cardName 的是子卡片问题
            16. 当 JSON 中存在数组（如 DiagList、SampleList），且数组元素的字段能匹配某个 SubCard 的问题时，使用 SubCard 映射
            17. 当 JSON 中不存在重复数组，但某个对象整体对应一个 SubCard 时，也可以使用 SubCard 映射
            18. SubCard 映射允许三种合法写法：A. sourcePath 输出相对 arrayPath 的字段路径；B. sourcePath 输出完整路径；C. 如果要读取当前主记录字段，sourcePath 必须写成 $main.xxx。三种写法都合法，但必须与 arrayPath 语义一致
            19. 如果 SubCard 容器是数组，arrayPath 填数组容器路径，不含 []、不含 [0]；如果 SubCard 容器是对象，arrayPath 填对象容器路径
            20. 如果样本根节点本身就是数组，且每个数组元素就是一行 SubCard 数据，则 arrayPath 固定填 "$"，sourcePath 只填写单条记录内字段路径，例如 ADMISSION_TIME_ICU；不要输出开头的 [] 或 $[]
            21. arrayPath 是 SubCard 容器本身的路径，不是容器内字段的路径；已填写 arrayPath 时，数组项内字段优先输出容器内相对路径；当前主记录字段必须输出为 $main.xxx；真实根字段必须输出为 $.xxx
            22. **重要**：targetField 必须严格填写目标字段列表中的真实字段ID本身，不可编造，不可改写，不可只填显示名，也不要写成 SubCard/字段ID、Question/字段ID 这种带类别前缀的格式
            23. **重要**：mappingTarget 必须严格使用目标字段列表中该字段所属的类别（Patient/Event/Question/SubCard），不可自行判断类别。例如目标字段列表中 "Question/chief_complaint" 的类别是 Question，则 mappingTarget 必须填 "Question"，而非 "Event"
            24. **重要**：必须严格遵守“当前操作范围”。如果当前范围未包含某个字段或某个类别，严禁输出该字段或该类别；找不到匹配时返回空数组 []
            25. 目标字段列表中如果附带了 hint、cardName 等补充信息，必须一起理解，不能只看显示名
            26. “完整叶子路径样本”用于补充深层字段；即使某个字段未在结构摘要中完整展开，只要它出现在完整叶子路径样本中，也必须参与匹配判断
            27. 如果额外提供了“相关叶子路径候选”，说明这些候选是从完整叶子集合中按当前目标字段召回出的优先参考项；即使它们没有完整出现在结构摘要或叶子样本中，也必须优先结合这些候选做判断
            28. 如果当前目标字段全部属于同一 SubCard，应先锁定最可能的重复数组簇，再在同一数组元素内成组匹配多个字段；如果明显是单个对象，则在同一对象内成组匹配
            29. 对同一 SubCard 中已经能确认的字段，必须先返回已确认部分；不要因为其余字段暂时无法判断，就整组返回空数组 []

            ## 输出格式
            严格输出 JSON 数组，每个元素：
            {"sourcePath":"JSON路径","targetField":"目标字段真实字段ID","mappingTarget":"Patient|Event|Question|SubCard","confidence":0.0-1.0,"reason":"简短中文理由","dictCode":null,"cardId":null,"arrayPath":null}

            ## 示例
            输入源: PatientInfo.Gender: "1" (String)
            目标: Patient/gender: 性别 (string)
            输出: {"sourcePath":"PatientInfo.Gender","targetField":"gender","mappingTarget":"Patient","confidence":0.95,"reason":"性别字段，值为编码需字典转换","dictCode":"gender_dict"}

            SubCard 数组示例：sourcePath=CommonOrder.TimeField，arrayPath=Request.Body.OrderGroupList。
            SubCard 根数组示例：如果样本根节点本身就是数组，且每个数组元素是一行子卡数据，则 sourcePath=TimeField，arrayPath=$。
            SubCard 对象示例：sourcePath=TimeField，arrayPath=Request.Body.CommonOrder。
            SubCard 主记录示例：如果子卡字段要取当前主记录中的 LAB_ITEM_NAME，则 sourcePath=$main.LAB_ITEM_NAME。

            只输出 JSON 数组，不要 markdown 代码块，不要解释文字。
            """;

        var userPrompt = $"""
            ## 源 JSON 结构（路径: "示例值" (类型)）
            {jsonStructure}

            {typedArrayHintText}

            {(string.IsNullOrWhiteSpace(sampleNote) ? string.Empty : $"## 样本补充说明\n{sampleNote}\n")}

            {mainRecordScopeText}

            {candidateHintText}

            {leafHintText}

            ## 当前操作范围
            {scopeRuleText}

            ## 目标字段列表（类别/字段ID: 显示名 (数据类型)）
            {targetFieldsText}

            请严格限制在上述操作范围内，逐一分析每个源字段，尽可能找到匹配的目标字段，输出映射建议 JSON 数组。
            """;

        var responseText = await CallLlmAsync(systemPrompt, userPrompt, ct);
        _logger.LogInformation("LLM 原始返回: {Response}", responseText.Length > 500 ? responseText[..500] + "..." : responseText);

        var suggestions = ParseSuggestions(responseText);
        NormalizeSuggestionsToTargetFields(suggestions, targetFields, analysisToken, mainRecordArrayPath);
        suggestions = PostProcessSuggestions(analysisToken, suggestions, mainRecordArrayPath);
        suggestions = DeduplicateSuggestions(suggestions);
        suggestions = FilterSuggestionsByTargetFields(suggestions, targetFields);
        return (suggestions, responseText);
    }

    private static bool ShouldRunFocusedRetry(List<TargetFieldInfo> targetFields)
    {
        if (targetFields.Count == 0 || targetFields.Count > 12)
        {
            return false;
        }

        return targetFields.All(f => f.Category is MappingTarget.Question or MappingTarget.SubCard);
    }

    private async Task<(List<MappingSuggestion> Suggestions, List<string> RawResponses)> CompleteMissingTargetSuggestionsAsync(
        JToken sampleJson,
        JToken analysisToken,
        string? sampleNote,
        List<TargetFieldInfo> targetFields,
        string? scopeHint,
        List<MappingSuggestion> currentSuggestions,
        CancellationToken ct,
        string? mainRecordArrayPath = null)
    {
        var coveredKeys = currentSuggestions
            .Select(s => GetTargetFieldKey(s.MappingTarget, s.TargetField, s.CardId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missingTargets = targetFields
            .Where(field => !coveredKeys.Contains(GetTargetFieldKey(field.Category, field.FieldId, field.CardId)))
            .ToList();

        if (missingTargets.Count == 0)
        {
            return (currentSuggestions, []);
        }

        var mergedSuggestions = new List<MappingSuggestion>(currentSuggestions);
        var rawResponses = new List<string>();

        foreach (var field in missingTargets)
        {
            var fieldScopeHint = string.IsNullOrWhiteSpace(scopeHint)
                ? $"当前字段 {field.DisplayName}（{field.FieldId}）"
                : $"{scopeHint} / 当前字段 {field.DisplayName}（{field.FieldId}）";

            var retryResult = await SuggestMappingsWithGeneralPromptAsync(
                sampleJson,
                analysisToken,
                sampleNote,
                [field],
                fieldScopeHint,
                ct,
                mainRecordArrayPath);

            rawResponses.Add(retryResult.RawResponse);

            if (retryResult.Suggestions.Count == 0)
            {
                continue;
            }

            mergedSuggestions.AddRange(retryResult.Suggestions);
        }

        mergedSuggestions = DeduplicateSuggestions(mergedSuggestions);
        mergedSuggestions = FilterSuggestionsByTargetFields(mergedSuggestions, targetFields);
        return (mergedSuggestions, rawResponses);
    }

    private static int CountCoveredTargets(List<MappingSuggestion> suggestions)
    {
        return suggestions
            .Select(s => GetTargetFieldKey(s.MappingTarget, s.TargetField, s.CardId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    private async Task<(List<MappingSuggestion> Suggestions, string RawResponse)> SuggestMappingsWithFocusedPromptAsync(
        JToken sampleJson,
        JToken analysisToken,
        List<TargetFieldInfo> targetFields,
        string? scopeHint,
        CancellationToken ct,
        string? mainRecordArrayPath = null)
    {
        var candidateText = BuildFocusedTargetCandidateText(analysisToken, targetFields);
        if (string.IsNullOrWhiteSpace(candidateText))
        {
            return ([], "[]");
        }

        var targetFieldsText = string.Join("\n", targetFields.Select(FormatTargetFieldForPrompt));
        var scopeText = string.IsNullOrWhiteSpace(scopeHint) ? "当前节点" : scopeHint.Trim();
        var mainRecordScopeText = BuildMainRecordScopePromptText(mainRecordArrayPath);
        var rootArrayNote = sampleJson is JArray
            ? "样本根节点是数组，当前候选源字段列表已经按单条记录展开；如果目标属于 SubCard，sourcePath 只填写单条记录内路径，arrayPath 可留空或填写 \"$\"。"
            : "样本根节点不是数组。";

        var systemPrompt = """
            你是一个只负责字段语义对齐的映射助手。

            ## 任务目标
            候选源字段列表已经经过本地拆词粗筛，只保留了与当前目标字段较相关的候选项。
            你现在做的是精排判断：在每个目标字段自己的候选源字段列表中，找出最合适的映射，不要分析列表外内容。

            ## 硬性规则
            1. sourcePath 只能从当前目标字段对应的候选源字段列表中逐字选择，禁止编造路径
            2. targetField 必须严格填写目标字段列表中的真实字段ID本身，不可编造，不可改写，不可只填显示名，也不要写成 SubCard/字段ID、Question/字段ID 这种带类别前缀的格式
            3. mappingTarget、cardId 必须与目标字段列表一致，不可自造
            4. 本地拆词只用于候选召回，不是最终判断依据；最终必须结合字段整体语义、上下文和数据类型做精排
            5. 如果多个候选共享相同公共词或处于相同上下文，重点比较它们之间剩余的区分部分，不要机械地按公共词重合数量决定
            6. 对每个目标字段独立判断；即使只能确认部分字段，也必须先返回已确认部分，不要因为其他字段不确定就返回空数组 []
            7. 当前候选列表已经是粗筛结果；如果其中存在明显最可能项，应优先返回最佳项，只有确实无法区分时才返回空

            ## 输出格式
            严格输出 JSON 数组，每个元素：
            {"sourcePath":"候选路径","targetField":"真实字段ID","mappingTarget":"Patient|Event|Question|SubCard","confidence":0.0-1.0,"reason":"简短中文理由","dictCode":null,"cardId":null,"arrayPath":null}

            只输出 JSON 数组，不要解释文字。
            """;

        var userPrompt = $"""
            ## 当前范围
            {scopeText}

            ## 样本说明
            {rootArrayNote}

            {mainRecordScopeText}

            ## 目标字段与候选源字段（已按字段名拆词做粗筛）
            {candidateText}

            ## 目标字段总表
            {targetFieldsText}

            请按“每个目标字段独立判断”的方式，只根据上面的候选源字段列表输出映射建议 JSON 数组。
            """;

        var responseText = await CallLlmAsync(systemPrompt, userPrompt, ct);
        _logger.LogInformation("LLM 二次重试原始返回: {Response}", responseText.Length > 500 ? responseText[..500] + "..." : responseText);

        var suggestions = ParseSuggestions(responseText);
        NormalizeSuggestionsToTargetFields(suggestions, targetFields, analysisToken, mainRecordArrayPath);
        suggestions = PostProcessSuggestions(analysisToken, suggestions, mainRecordArrayPath);
        suggestions = DeduplicateSuggestions(suggestions);
        suggestions = FilterSuggestionsByTargetFields(suggestions, targetFields);
        return (suggestions, responseText);
    }

    /// <summary>
    /// 分析 JSON 示例 + 文档描述，生成接口配置建议
    /// </summary>
    public async Task<InterfaceConfigSuggestion?> AnalyzeInterfaceConfigAsync(
        string sampleJson, string documentDescription, IReadOnlyCollection<string>? eventTypeNames, CancellationToken ct)
    {
        var parsed = JToken.Parse(sampleJson);
        var (analysisToken, sampleNote) = PrepareSingleRecordAnalysisToken(parsed);
        var rawJsonStructure = BuildJsonStructure(parsed, "", 0);
        var jsonStructure = BuildJsonStructure(analysisToken, "", 0);
        var typedArrayHintText = BuildTypedArrayFilterHintText(analysisToken);
        var codeCandidateHintText = BuildCodeCandidateHintText(parsed);
        var mainRecordArrayHintText = BuildMainRecordArrayHintText(parsed);
        var eventTypeText = BuildEventTypePromptText(eventTypeNames);

        var systemPrompt = """
            你是医疗数据集成专家，负责分析医院 HIS/LIS/EMR 系统的 JSON 消息结构，提取接口配置信息。

            ## 任务
            根据提供的 JSON 结构和接口文档描述，分析并输出以下字段：
            - TranCode: 接口事件代码。必须先判断 JSON 中 code 类字段是否已有接口/消息/业务事件代码；有可信值时直接使用该值，没有可信值时再根据文档描述或 JSON 内容推断简短英文标识
            - TranName: 接口事件名称（简短中文描述，如"入院登记"、"医嘱下达"）
            - EventTypeName: 平台事件类型，必须从“可用事件类型”列表中选择；无法判断时输出空字符串
            - MainRecordArrayPath: 主记录数组路径。只有消息包含批量主记录数组时填写；单条对象不要填写
            - MrnSourcePath: 病案号/病历号在 JSON 中的路径（如 Data.PatientInfo.MRN）。如果已填写 MainRecordArrayPath，必须优先输出主记录内相对路径
            - EventStartTimeSourcePath: 事件开始时间在 JSON 中的路径（如 Data.VisitInfo.AdmissionDate）。如果已填写 MainRecordArrayPath，必须优先输出主记录内相对路径
            - Description: 接口用途简要说明

            ## 推断顺序
            1. 先判断 TranCode：优先从 JSON 中接口/消息级 code 字段取原值；只有没有可信 code 时才自行推断
            2. 再判断 MainRecordArrayPath：批量主记录数组填写数组容器路径；根节点本身是主记录数组时填写 "$"；单条对象输出空字符串
            3. 再从可用事件类型中选择 EventTypeName
            4. 最后基于 MainRecordArrayPath 和 EventTypeName 生成 MrnSourcePath / EventStartTimeSourcePath

            ## 事件开始时间规则
            - EventTypeName 为“住院”时，事件开始时间优先选择入院时间、住院时间、入科时间等字段，不要选择出院时间、结算时间
            - EventTypeName 为“手术”时，事件开始时间优先选择手术开始时间、手术时间等字段，不要选择入院时间、申请时间

            ## 路径格式
            - 使用不带 $ 前缀的点分路径（如 Data.PatientInfo.MRN）
            - 如果样本根路径包含 Request.Body，则输出时必须保留 Request.Body 前缀
            - 如果样本根节点本身是数组，则输出路径必须以“单条记录”为基准，不要带开头的 []
            - 数组可以用 [0]，也可以用 JSONPath 过滤表达式 [?(...)]
            - 如果病案号来自标识数组（如 PatientIdentifierList/IdentifierList），优先输出按类型过滤后的路径，而不是盲目取 [0]
            - 如果样本中提供了“数组过滤候选路径”，优先从这些候选路径里挑选最匹配 MrnSourcePath 的结果
            - 如果 MainRecordArrayPath 非空，MrnSourcePath 和 EventStartTimeSourcePath 应输出主记录对象内部的相对路径，如 PATIENT_ID、ADMISSION_TIME；找不到主记录内字段时输出空字符串，不要用根完整路径替代
            - MainRecordArrayPath 是数组容器路径，不带 []；例如 Request.Body.DataList，不要写 Request.Body.DataList[]

            ## 输出格式
            严格输出一个 JSON 对象，不要 markdown 代码块，不要解释文字：
            {"TranCode":"...","TranName":"...","EventTypeName":"...","MainRecordArrayPath":"...","MrnSourcePath":"...","EventStartTimeSourcePath":"...","Description":"..."}

            ## 注意
            - 找不到的字段输出空字符串，不要编造路径
            - TranCode 长度不超过 20 个字符
            - TranName 长度不超过 100 个字符
            - EventTypeName 只能使用可用事件类型列表中的原始名称
            """;

        var userPrompt = $"""
            ## 可用事件类型
            {eventTypeText}

            ## 原始 JSON 结构（用于判断主记录数组路径）
            {rawJsonStructure}

            ## JSON 结构（路径: "示例值" (类型)）
            {jsonStructure}

            {codeCandidateHintText}

            {mainRecordArrayHintText}

            {typedArrayHintText}

            {(string.IsNullOrWhiteSpace(sampleNote) ? string.Empty : $"## 样本补充说明\n{sampleNote}\n")}

            ## 接口文档描述
            {(string.IsNullOrWhiteSpace(documentDescription) ? "（未提供）" : documentDescription)}

            请分析以上信息，输出接口配置建议 JSON 对象。
            """;

        try
        {
            var responseText = await CallLlmAsync(systemPrompt, userPrompt, ct);
            _logger.LogInformation("LLM 接口配置分析返回: {Response}", responseText.Length > 500 ? responseText[..500] + "..." : responseText);

            // 提取 JSON 对象
            var startIndex = responseText.IndexOf('{');
            var endIndex = responseText.LastIndexOf('}');
            if (startIndex < 0 || endIndex < 0)
            {
                _logger.LogWarning("LLM 返回内容中未找到 JSON 对象");
                return null;
            }

            var jsonObj = responseText[startIndex..(endIndex + 1)];
            using var doc = JsonDocument.Parse(jsonObj);
            var root = doc.RootElement;

            var result = new InterfaceConfigSuggestion
            {
                TranCode = root.TryGetProperty("TranCode", out var tc) ? tc.GetString() ?? "" : "",
                TranName = root.TryGetProperty("TranName", out var tn) ? tn.GetString() ?? "" : "",
                EventTypeName = root.TryGetProperty("EventTypeName", out var et) ? et.GetString() ?? "" : "",
                MainRecordArrayPath = root.TryGetProperty("MainRecordArrayPath", out var mrp) ? mrp.GetString() ?? "" : "",
                MrnSourcePath = root.TryGetProperty("MrnSourcePath", out var mrn) ? mrn.GetString() ?? "" : "",
                EventStartTimeSourcePath = root.TryGetProperty("EventStartTimeSourcePath", out var est) ? est.GetString() ?? "" : "",
                Description = root.TryGetProperty("Description", out var desc) ? desc.GetString() ?? "" : "",
            };
            NormalizeInterfaceConfigSuggestion(parsed, result, eventTypeNames);
            return result;
        }
        catch (Exception ex) when (ex is not HttpRequestException and not OperationCanceledException and not TimeoutException)
        {
            _logger.LogWarning(ex, "LLM 接口配置分析失败");
            return null;
        }
    }

    /// <summary>
    /// 单条推荐：根据上下文推荐字段
    /// </summary>
    public async Task<string?> SuggestFieldAsync(
        string jsonStructure, string fieldHint, string direction, CancellationToken ct)
    {
        try
        {
            var systemPrompt = "你是医疗数据字段映射专家。根据提示推荐最合适的字段，只返回字段名，不要其他文字。";
            var userPrompt = $"JSON 结构:\n{jsonStructure}\n\n方向: {direction}\n提示: {fieldHint}\n\n请推荐最合适的字段名:";

            return await CallLlmAsync(systemPrompt, userPrompt, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LLM 单条推荐失败");
            return null;
        }
    }

    /// <summary>
    /// 调用 Ollama OpenAI 兼容 API
    /// </summary>
    private async Task<string> CallLlmAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        var options = await GetEffectiveOptionsAsync();
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            throw new InvalidOperationException("未配置 LLM BaseUrl");
        }

        var url = $"{options.BaseUrl.TrimEnd('/')}/chat/completions";
        _logger.LogInformation("LLM 请求: {Url}, Model: {Model}", url, options.Model);

        var requestBody = new
        {
            model = options.Model,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            temperature = 0.1,
            max_tokens = 8192,
        };

        var json = JsonSerializer.Serialize(requestBody);
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        // 云端 API 需要 Bearer Token
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", options.ApiKey);

        string responseJson;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        try
        {
            using var response = await _httpClient.SendAsync(request, timeoutCts.Token);
            responseJson = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("LLM 响应错误: {StatusCode}, Body: {Body}", response.StatusCode, responseJson);
                throw new HttpRequestException($"LLM 返回 {(int)response.StatusCode}: {responseJson}");
            }
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException("LLM 请求已取消", ex, ct);
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException($"LLM 请求超时（{options.TimeoutSeconds} 秒），请调高本地 LLM 超时时间或检查模型是否响应。", ex);
        }

        _logger.LogDebug("LLM 原始响应: {Response}", responseJson.Length > 1000 ? responseJson[..1000] + "..." : responseJson);

        using var doc = JsonDocument.Parse(responseJson);
        var messageContent = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        return messageContent?.Trim() ?? "";
    }

    private async Task<LlmOptions> GetEffectiveOptionsAsync()
    {
        var options = await _configService.GetLlmOptionsAsync(_defaultOptions);
        if (options.TimeoutSeconds <= 0)
        {
            options.TimeoutSeconds = _defaultOptions.TimeoutSeconds;
        }

        return options;
    }

    private static string BuildEventTypePromptText(IReadOnlyCollection<string>? eventTypeNames)
    {
        var names = eventTypeNames?
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        return names.Count == 0
            ? "（当前项目没有可用事件类型；EventTypeName 必须输出空字符串）"
            : string.Join("\n", names.Select(n => $"- {n}"));
    }

    private static string BuildCodeCandidateHintText(JToken sampleJson)
    {
        var candidates = new List<CodeCandidateHint>();
        CollectCodeCandidateHints(sampleJson, "", 0, candidates);

        var lines = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.Value))
            .GroupBy(c => $"{c.Path}|{c.Value}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(20)
            .Select(c => $"- {c.Path}: \"{TruncateForPrompt(c.Value)}\"");

        var text = string.Join("\n", lines);
        return string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : $"""
                ## JSON 中的 code 类候选字段
                以下字段仅供判断 TranCode。请自行判断哪个是接口/消息级事件代码，不要把科室代码、医嘱代码、诊断代码等明细业务代码误当 TranCode。
                {text}
                """;
    }

    private static void CollectCodeCandidateHints(JToken token, string path, int depth, List<CodeCandidateHint> candidates)
    {
        if (depth > MaxLeafHintDepth)
            return;

        switch (token)
        {
            case JObject obj:
                foreach (var prop in obj.Properties())
                {
                    var currentPath = string.IsNullOrWhiteSpace(path) ? prop.Name : $"{path}.{prop.Name}";
                    if (prop.Value is JObject or JArray)
                    {
                        CollectCodeCandidateHints(prop.Value, currentPath, depth + 1, candidates);
                    }
                    else if (IsCodeCandidateName(prop.Name))
                    {
                        candidates.Add(new CodeCandidateHint(currentPath, prop.Value.ToString()));
                    }
                }
                break;

            case JArray array:
                var sampleCount = Math.Min(array.Count, MaxArraySampleCount);
                for (var i = 0; i < sampleCount; i++)
                {
                    var itemPath = string.IsNullOrWhiteSpace(path) ? "[]" : $"{path}[]";
                    CollectCodeCandidateHints(array[i], itemPath, depth + 1, candidates);
                }
                break;
        }
    }

    private static bool IsCodeCandidateName(string propertyName)
    {
        var normalized = propertyName.Trim().ToLowerInvariant();
        return normalized.Contains("code", StringComparison.Ordinal)
            || normalized is "trancode" or "servercode" or "eventcode" or "msgtype" or "messagetype";
    }

    private static string BuildMainRecordArrayHintText(JToken sampleJson)
    {
        var hints = new List<MainRecordArrayHint>();
        CollectMainRecordArrayHints(sampleJson, "", 0, hints);

        var lines = hints
            .GroupBy(h => h.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(12)
            .Select(h => $"- {h.Path}: {h.Count} 项；首项字段：{h.SampleKeys}");

        var text = string.Join("\n", lines);
        return string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : $"""
                ## 主记录数组候选
                仅当某个数组代表需要逐条处理的主记录时，MainRecordArrayPath 才选择该数组容器路径；单条对象保持空字符串。
                {text}
                """;
    }

    private static void CollectMainRecordArrayHints(JToken token, string path, int depth, List<MainRecordArrayHint> hints)
    {
        if (depth > MaxJsonDepth + 2)
            return;

        switch (token)
        {
            case JObject obj:
                foreach (var prop in obj.Properties())
                {
                    var currentPath = string.IsNullOrWhiteSpace(path) ? prop.Name : $"{path}.{prop.Name}";
                    CollectMainRecordArrayHints(prop.Value, currentPath, depth + 1, hints);
                }
                break;

            case JArray array:
                if (array.OfType<JObject>().FirstOrDefault() is { } firstObj)
                {
                    var arrayPath = string.IsNullOrWhiteSpace(path) ? "$" : SubCardPathHelper.NormalizeArrayContainerPath(path);
                    var sampleKeys = string.Join(", ", firstObj.Properties().Take(12).Select(p => p.Name));
                    hints.Add(new MainRecordArrayHint(arrayPath, array.Count, sampleKeys));
                }

                var sampleCount = Math.Min(array.Count, MaxArraySampleCount);
                for (var i = 0; i < sampleCount; i++)
                {
                    var itemPath = string.IsNullOrWhiteSpace(path) ? "[]" : $"{path}[]";
                    CollectMainRecordArrayHints(array[i], itemPath, depth + 1, hints);
                }
                break;
        }
    }

    private static void NormalizeInterfaceConfigSuggestion(
        JToken sampleJson,
        InterfaceConfigSuggestion result,
        IReadOnlyCollection<string>? eventTypeNames)
    {
        result.EventTypeName = NormalizeSuggestedEventType(result.EventTypeName, eventTypeNames);
        result.MainRecordArrayPath = NormalizeSuggestedMainRecordArrayPath(sampleJson, result.MainRecordArrayPath);

        if (!string.IsNullOrWhiteSpace(result.MainRecordArrayPath))
        {
            result.MrnSourcePath = NormalizeSuggestedMainRecordRelativePath(result.MrnSourcePath, result.MainRecordArrayPath);
            result.EventStartTimeSourcePath = NormalizeSuggestedMainRecordRelativePath(result.EventStartTimeSourcePath, result.MainRecordArrayPath);
        }
    }

    private static string NormalizeSuggestedEventType(string? value, IReadOnlyCollection<string>? eventTypeNames)
    {
        var normalized = value?.Trim() ?? "";
        var names = eventTypeNames?
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        if (string.IsNullOrWhiteSpace(normalized) || names.Count == 0)
            return "";

        var exact = names.FirstOrDefault(n => string.Equals(n, normalized, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact;

        var fuzzyMatches = names
            .Where(n => normalized.Contains(n, StringComparison.OrdinalIgnoreCase)
                        || n.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return fuzzyMatches.Count == 1 ? fuzzyMatches[0] : "";
    }

    private static string NormalizeSuggestedMainRecordArrayPath(JToken sampleJson, string? value)
    {
        var original = value?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(original))
            return "";

        if (original is "[]" or "$[]")
            return sampleJson is JArray ? "$" : "";

        var normalized = SubCardPathHelper.NormalizeArrayContainerPath(original);
        if (string.IsNullOrWhiteSpace(normalized))
            return "";

        if (SubCardPathHelper.IsRootContainerPath(normalized))
            return sampleJson is JArray ? "$" : "";

        return MessageJsonHelper.SafeSelectToken(sampleJson, normalized) is JArray
            ? normalized
            : "";
    }

    private static string NormalizeSuggestedMainRecordRelativePath(string? value, string mainRecordArrayPath)
    {
        var normalized = value?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(normalized))
            return "";

        if (MessageJsonHelper.TryNormalizeMainRecordRelativeSourcePath(normalized, mainRecordArrayPath, out var relativePath))
            return relativePath;

        if (SubCardPathHelper.IsAbsoluteJsonPath(normalized) || MessageJsonHelper.IsMainRecordScopedPath(normalized))
            return "";

        return normalized;
    }

    /// <summary>
    /// 构建简化的 JSON 结构描述
    /// </summary>
    private static string BuildJsonStructure(JToken token, string path, int depth)
    {
        if (depth > MaxJsonDepth) return ""; // 限制深度

        var sb = new StringBuilder();

        if (token is JObject obj)
        {
            foreach (var prop in obj.Properties())
            {
                var currentPath = string.IsNullOrEmpty(path) ? prop.Name : $"{path}.{prop.Name}";

                if (prop.Value is JObject || prop.Value is JArray)
                {
                    sb.AppendLine($"{currentPath}: [{prop.Value.Type}]");
                    sb.Append(BuildJsonStructure(prop.Value, currentPath, depth + 1));
                }
                else
                {
                    var sampleValue = prop.Value.ToString();
                    if (sampleValue.Length > 50) sampleValue = sampleValue[..50] + "...";
                    sb.AppendLine($"{currentPath}: \"{sampleValue}\" ({prop.Value.Type})");
                }
            }
        }
        else if (token is JArray arr)
        {
            sb.AppendLine($"{path}[]: ({arr.Count} 项)");
            if (arr.Count > 0)
            {
                sb.Append(BuildJsonStructure(arr[0], $"{path}[]", depth + 1));
            }

            if (arr.Count > 1)
            {
                sb.AppendLine($"{path}[...]: (结构按首项展开，共 {arr.Count} 项)");
            }
        }
        else
        {
            var sampleValue = token.ToString();
            if (sampleValue.Length > 50) sampleValue = sampleValue[..50] + "...";
            sb.AppendLine($"{path}: \"{sampleValue}\" ({token.Type})");
        }

        return sb.ToString();
    }

    private static string BuildLeafHintText(JToken sampleJson)
    {
        var selected = GetSelectedLeafHints(sampleJson);
        if (selected.Count == 0)
        {
            return string.Empty;
        }

        var lines = string.Join("\n", selected.Select(h =>
            $"- {h.Path}: \"{TruncateForPrompt(h.SampleValue)}\" ({h.ValueType})"));

        return $"""
            ## 完整叶子路径样本（补充深层字段，路径已归一化）
            {lines}
            """;
    }

    private static string BuildFocusedTargetCandidateText(JToken sampleJson, List<TargetFieldInfo> targetFields)
    {
        return BuildTargetCandidateText(
            sampleJson,
            targetFields,
            FocusedCandidateCountPerTarget,
            MaxFocusedCandidateCountPerTarget);
    }

    private static string BuildGeneralPromptCandidateHintText(JToken sampleJson, List<TargetFieldInfo> targetFields)
    {
        if (targetFields.Count == 0 || targetFields.Count > MaxGeneralPromptCandidateHintTargetCount)
        {
            return string.Empty;
        }

        var candidateText = BuildTargetCandidateText(
            sampleJson,
            targetFields,
            GeneralCandidateHintCountPerTarget,
            MaxGeneralCandidateHintCountPerTarget);

        if (string.IsNullOrWhiteSpace(candidateText))
        {
            return string.Empty;
        }

        return $"""
            ## 相关叶子路径候选（从完整叶子集合按当前目标字段召回，未受结构摘要和叶子样本截断限制）
            {candidateText}
            """;
    }

    private static string BuildTargetCandidateText(
        JToken sampleJson,
        List<TargetFieldInfo> targetFields,
        int candidateCountPerTarget,
        int maxCandidateCountPerTarget)
    {
        var allLeaves = GetDistinctLeafHints(sampleJson);
        if (allLeaves.Count == 0 || targetFields.Count == 0)
        {
            return string.Empty;
        }

        var sections = targetFields
            .Select(field => BuildTargetCandidateSection(field, allLeaves, candidateCountPerTarget, maxCandidateCountPerTarget))
            .Where(static section => !string.IsNullOrWhiteSpace(section))
            .ToList();

        return sections.Count == 0 ? string.Empty : string.Join("\n\n", sections);
    }

    private static List<LeafHint> GetSelectedLeafHints(JToken sampleJson)
    {
        return GetDistinctLeafHints(sampleJson)
            .Take(MaxLeafHintCount)
            .ToList();
    }

    private static List<LeafHint> GetDistinctLeafHints(JToken sampleJson)
    {
        var leaves = new List<LeafHint>();
        CollectLeafHints(sampleJson, "", 0, leaves);

        return leaves
            .Where(h => !string.IsNullOrWhiteSpace(h.Path))
            .GroupBy(h => h.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g
                .OrderByDescending(x => x.Priority)
                .ThenByDescending(x => x.SampleValue.Length)
                .First())
            .OrderByDescending(h => h.Priority)
            .ThenBy(h => h.Path.Length)
            .ToList();
    }

    private static string BuildTargetCandidateSection(
        TargetFieldInfo field,
        List<LeafHint> selectedLeaves,
        int candidateCountPerTarget,
        int maxCandidateCountPerTarget)
    {
        var candidates = GetCandidateLeafHintsForTarget(selectedLeaves, field, candidateCountPerTarget, maxCandidateCountPerTarget);
        if (candidates.Count == 0)
        {
            return string.Empty;
        }

        var header = FormatTargetFieldForPrompt(field);
        var candidateLines = string.Join("\n", candidates.Select(h =>
            $"  - {h.Path}: \"{TruncateForPrompt(h.SampleValue)}\" ({h.ValueType})"));

        return $"""
            {header}
              候选源字段:
            {candidateLines}
            """;
    }

    private static List<LeafHint> GetCandidateLeafHintsForTarget(
        List<LeafHint> selectedLeaves,
        TargetFieldInfo field,
        int candidateCountPerTarget,
        int maxCandidateCountPerTarget)
    {
        var targetTokens = ExtractFieldTokens(field);
        if (targetTokens.Count == 0)
        {
            return [];
        }

        var expandedTargetTokens = ExpandEquivalentTokens(targetTokens);
        var ranked = selectedLeaves
            .Select(leaf => new
            {
                Leaf = leaf,
                Score = ComputeTokenMatchScore(
                    targetTokens,
                    expandedTargetTokens,
                    ExtractPathTokens(leaf.Path))
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Leaf.Priority)
            .ThenBy(x => x.Leaf.Path.Length)
            .ToList();

        if (ranked.Count == 0)
        {
            return [];
        }

        var takeCount = Math.Min(candidateCountPerTarget, ranked.Count);
        var cutoffScore = ranked[takeCount - 1].Score;
        var focused = ranked
            .TakeWhile(x => x.Score >= cutoffScore)
            .Take(maxCandidateCountPerTarget)
            .Select(x => x.Leaf)
            .ToList();

        return focused;
    }

    private static int ComputeTokenMatchScore(
        HashSet<string> exactTargetTokens,
        HashSet<string> expandedTargetTokens,
        HashSet<string> exactSourceTokens)
    {
        if (exactTargetTokens.Count == 0 || exactSourceTokens.Count == 0)
        {
            return 0;
        }

        var exactOverlapCount = exactTargetTokens.Count(exactSourceTokens.Contains);
        var expandedSourceTokens = ExpandEquivalentTokens(exactSourceTokens);
        var expandedOverlapCount = expandedTargetTokens.Count(expandedSourceTokens.Contains);

        // 精确命中优先，同义词命中只做补充，避免“住院天数”压过“ICU天数”。
        return exactOverlapCount * 100 + expandedOverlapCount;
    }

    private static HashSet<string> ExpandEquivalentTokens(HashSet<string> tokens)
    {
        if (tokens.Count == 0)
        {
            return [];
        }

        var expanded = new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase);
        foreach (var token in tokens)
        {
            if (!TokenSynonymMap.TryGetValue(token, out var synonyms))
            {
                continue;
            }

            expanded.UnionWith(synonyms);
        }

        return expanded;
    }

    private static Dictionary<string, string[]> BuildTokenSynonymMap()
    {
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in TokenSynonymGroups)
        {
            var normalizedGroup = group
                .SelectMany(ExtractTokens)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var token in normalizedGroup)
            {
                map[token] = normalizedGroup;
            }
        }

        return map;
    }

    private static HashSet<string> ExtractFieldTokens(TargetFieldInfo field)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in new[]
                 {
                     field.FieldId,
                     field.DisplayName,
                     field.SemanticHint,
                     field.CardName
                 })
        {
            foreach (var token in ExtractTokens(part))
            {
                tokens.Add(token);
            }
        }

        return tokens;
    }

    private static HashSet<string> ExtractPathTokens(string? path)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in ExtractTokens(path))
        {
            tokens.Add(token);
        }

        return tokens;
    }

    private static IEnumerable<string> ExtractTokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        var normalized = text
            .Replace('.', ' ')
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Replace('/', ' ')
            .Replace('[', ' ')
            .Replace(']', ' ');

        foreach (Match match in TokenRegex.Matches(normalized))
        {
            var token = match.Value.Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(token))
            {
                yield return token;
            }
        }
    }

    private static void CollectLeafHints(JToken token, string path, int depth, List<LeafHint> leaves)
    {
        if (depth > MaxLeafHintDepth)
        {
            return;
        }

        switch (token)
        {
            case JObject obj:
                foreach (var prop in obj.Properties())
                {
                    var currentPath = string.IsNullOrEmpty(path) ? prop.Name : $"{path}.{prop.Name}";
                    CollectLeafHints(prop.Value, currentPath, depth + 1, leaves);
                }
                break;

            case JArray arr:
                var sampleCount = Math.Min(arr.Count, MaxArraySampleCount);
                for (var i = 0; i < sampleCount; i++)
                {
                    CollectLeafHints(arr[i], $"{path}[{i}]", depth + 1, leaves);
                }
                break;

            default:
                if (!TryGetLeafSampleValue(token, out var sampleValue))
                {
                    return;
                }

                var normalizedPath = SubCardPathHelper.NormalizeArrayPath(path);
                if (string.IsNullOrWhiteSpace(normalizedPath))
                {
                    return;
                }

                leaves.Add(new LeafHint(
                    normalizedPath,
                    sampleValue,
                    token.Type.ToString(),
                    ComputeLeafPriority(normalizedPath, sampleValue)));
                break;
        }
    }

    private static bool TryGetLeafSampleValue(JToken token, out string sampleValue)
    {
        sampleValue = token.Type switch
        {
            JTokenType.Null or JTokenType.Undefined => "",
            _ => token.ToString().Trim()
        };

        return true;
    }

    private static int ComputeLeafPriority(string path, string sampleValue)
    {
        var priority = 1;
        if (path.Contains("[]", StringComparison.Ordinal))
        {
            priority += 2;
        }

        if (sampleValue.Length <= 20)
        {
            priority += 1;
        }

        return priority;
    }

    private List<MappingSuggestion> PostProcessSuggestions(JToken sampleJson, List<MappingSuggestion> suggestions, string? mainRecordArrayPath)
    {
        foreach (var suggestion in suggestions)
        {
            TryCompleteRootArraySubCardSuggestion(sampleJson, suggestion);
        }

        return suggestions
            .Where(s => !string.IsNullOrWhiteSpace(s.SourcePath) && !string.IsNullOrWhiteSpace(s.TargetField))
            .Where(s => SuggestionPathExists(sampleJson, s, mainRecordArrayPath))
            .GroupBy(s => new
            {
                s.SourcePath,
                s.TargetField,
                s.MappingTarget,
                CardId = s.CardId?.ToString(),
                s.ArrayPath
            })
            .Select(g => g.OrderByDescending(x => x.Confidence).First())
            .OrderByDescending(s => s.Confidence)
            .ToList();
    }

    private static void TryCompleteRootArraySubCardSuggestion(JToken sampleJson, MappingSuggestion suggestion)
    {
        if (sampleJson is not JArray rootArray
            || rootArray.Count == 0
            || suggestion.MappingTarget != MappingTarget.SubCard
            || !string.IsNullOrWhiteSpace(suggestion.ArrayPath))
        {
            return;
        }

        var normalizedSourcePath = SubCardPathHelper.NormalizeArrayPath(suggestion.SourcePath);
        if (string.IsNullOrWhiteSpace(normalizedSourcePath)
            || SubCardPathHelper.IsAbsoluteJsonPath(normalizedSourcePath)
            || SubCardPathHelper.HasArrayWildcard(normalizedSourcePath))
        {
            return;
        }

        if (PathExists(rootArray[0], normalizedSourcePath))
        {
            suggestion.ArrayPath = "$";
        }
    }

    private static bool SuggestionPathExists(JToken sampleJson, MappingSuggestion suggestion, string? mainRecordArrayPath)
    {
        var normalizedSourcePath = SubCardPathHelper.NormalizeArrayPath(suggestion.SourcePath);
        if (string.IsNullOrWhiteSpace(normalizedSourcePath))
        {
            return false;
        }

        var mainContext = MessageJsonHelper.ResolveMainRecordContext(sampleJson, mainRecordArrayPath);
        if (suggestion.MappingTarget != MappingTarget.SubCard)
        {
            return MessageJsonHelper.ResolveFirstScopedToken(sampleJson, mainContext, normalizedSourcePath, mainContext) != null;
        }

        if (MessageJsonHelper.IsMainRecordScopedPath(normalizedSourcePath))
        {
            return MessageJsonHelper.ResolveFirstScopedToken(sampleJson, mainContext, normalizedSourcePath, mainContext) != null;
        }

        var effectiveArrayPath = SubCardPathHelper.ExpandArrayPathToRoot(sampleJson, suggestion.ArrayPath, mainRecordArrayPath);
        if (SubCardPathHelper.IsAbsoluteJsonPath(normalizedSourcePath)
            || (!string.IsNullOrWhiteSpace(effectiveArrayPath)
                && SubCardPathHelper.IsRootScopedPath(normalizedSourcePath, effectiveArrayPath)))
        {
            return PathExists(sampleJson, normalizedSourcePath);
        }

        if (string.IsNullOrWhiteSpace(effectiveArrayPath))
        {
            return false;
        }

        var context = SubCardPathHelper.ResolveFirstSubCardContext(sampleJson, effectiveArrayPath);
        return context != null && SubCardPathHelper.ResolveFirstToken(context, normalizedSourcePath) != null;
    }

    private static List<MappingSuggestion> DeduplicateSuggestions(List<MappingSuggestion> suggestions)
    {
        return suggestions
            .Where(s => !string.IsNullOrWhiteSpace(s.SourcePath) && !string.IsNullOrWhiteSpace(s.TargetField))
            .GroupBy(s => new
            {
                s.SourcePath,
                s.TargetField,
                s.MappingTarget,
                CardId = s.CardId?.ToString(),
                s.ArrayPath
            })
            .Select(g => g.OrderByDescending(x => x.Confidence).First())
            .OrderByDescending(s => s.Confidence)
            .ToList();
    }

    private void NormalizeSuggestionsToTargetFields(
        List<MappingSuggestion> suggestions,
        List<TargetFieldInfo> targetFields,
        JToken sampleJson,
        string? mainRecordArrayPath)
    {
        var candidateMap = targetFields
            .GroupBy(f => f.FieldId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var suggestion in suggestions)
        {
            var normalizedTargetField = NormalizeSuggestedTargetField(suggestion.TargetField);
            if (!string.Equals(suggestion.TargetField, normalizedTargetField, StringComparison.Ordinal))
            {
                _logger.LogInformation("LLM targetField 归一化: {Original} => {Normalized}", suggestion.TargetField, normalizedTargetField);
                suggestion.TargetField = normalizedTargetField;
            }

            if (!candidateMap.TryGetValue(suggestion.TargetField, out var candidates))
            {
                continue;
            }

            var matchedTarget = ResolveTargetFieldCandidate(suggestion, candidates);
            if (matchedTarget == null)
            {
                continue;
            }

            if (suggestion.MappingTarget != matchedTarget.Category)
            {
                _logger.LogWarning("LLM 类别修正: {Field} 从 {Wrong} 改为 {Correct}", suggestion.TargetField, suggestion.MappingTarget, matchedTarget.Category);
                suggestion.MappingTarget = matchedTarget.Category;
            }

            if (matchedTarget.Category == MappingTarget.SubCard && suggestion.CardId != matchedTarget.CardId)
            {
                _logger.LogWarning("LLM 子卡归属修正: {Field} 从 {WrongCardId} 改为 {CorrectCardId}", suggestion.TargetField, suggestion.CardId, matchedTarget.CardId);
                suggestion.CardId = matchedTarget.CardId;
            }

            if (matchedTarget.Category == MappingTarget.SubCard)
            {
                NormalizeSubCardSuggestion(suggestion, matchedTarget, sampleJson, mainRecordArrayPath);
            }
            else
            {
                suggestion.CardId = null;
                suggestion.ArrayPath = null;
            }
        }
    }

    private static string NormalizeSuggestedTargetField(string? targetField)
    {
        var normalized = targetField?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "";
        }

        var colonIndex = normalized.IndexOf(':');
        if (colonIndex > 0)
        {
            normalized = normalized[..colonIndex].Trim();
        }

        var slashIndex = normalized.IndexOf('/');
        if (slashIndex > 0)
        {
            var prefix = normalized[..slashIndex].Trim();
            if (prefix.Equals(nameof(MappingTarget.Patient), StringComparison.OrdinalIgnoreCase)
                || prefix.Equals(nameof(MappingTarget.Event), StringComparison.OrdinalIgnoreCase)
                || prefix.Equals(nameof(MappingTarget.Question), StringComparison.OrdinalIgnoreCase)
                || prefix.Equals(nameof(MappingTarget.SubCard), StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[(slashIndex + 1)..].Trim();
            }
        }

        return normalized;
    }

    private void NormalizeSubCardSuggestion(
        MappingSuggestion suggestion,
        TargetFieldInfo targetField,
        JToken sampleJson,
        string? mainRecordArrayPath)
    {
        suggestion.SourcePath = SubCardPathHelper.NormalizeArrayPath(suggestion.SourcePath);
        suggestion.ArrayPath = NormalizeSuggestionArrayPath(suggestion.ArrayPath, mainRecordArrayPath);
        TryPreferContextualSubCardSourcePath(sampleJson, suggestion, targetField);

        if (MessageJsonHelper.IsMainRecordScopedPath(suggestion.SourcePath))
        {
            suggestion.SourcePath = MessageJsonHelper.EnsureMainRecordScopedPath(suggestion.SourcePath);
            return;
        }

        if (!string.IsNullOrWhiteSpace(suggestion.ArrayPath)
            && TryNormalizeSubCardSourceByArrayPath(sampleJson, suggestion, mainRecordArrayPath))
        {
            return;
        }

        if (TryInferSubCardContainerPath(
                sampleJson,
                suggestion.SourcePath,
                mainRecordArrayPath,
                string.IsNullOrWhiteSpace(suggestion.ArrayPath),
                out var inferredArrayPath,
                out var inferredRelativePath))
        {
            if (string.IsNullOrWhiteSpace(inferredRelativePath))
            {
                _logger.LogWarning("LLM 子卡建议缺少容器内字段: {Path}", suggestion.SourcePath);
                suggestion.SourcePath = "";
                suggestion.ArrayPath = inferredArrayPath;
                return;
            }

            if (!string.IsNullOrWhiteSpace(suggestion.ArrayPath)
                && !SubCardPathHelper.PathsEqual(suggestion.ArrayPath, inferredArrayPath))
            {
                _logger.LogWarning("LLM 子卡 ArrayPath 修正: {Field} 从 {Wrong} 改为 {Correct}", suggestion.TargetField, suggestion.ArrayPath, inferredArrayPath);
            }

            suggestion.ArrayPath = inferredArrayPath;
            suggestion.SourcePath = inferredRelativePath;
            return;
        }

        if (MessageJsonHelper.TryNormalizeMainRecordSourcePath(suggestion.SourcePath, mainRecordArrayPath, out var mainRecordScopedPath)
            && !SubCardPathHelper.HasArrayWildcard(MessageJsonHelper.TrimMainRecordScopePrefix(mainRecordScopedPath)))
        {
            suggestion.SourcePath = mainRecordScopedPath;
            return;
        }

        if (SubCardPathHelper.TrySplitWildcardPath(suggestion.SourcePath, out var pickedArrayPath, out var relativePath))
        {
            pickedArrayPath = NormalizeSuggestionArrayPath(pickedArrayPath, mainRecordArrayPath) ?? pickedArrayPath;
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                _logger.LogWarning("LLM 子卡建议缺少数组项内字段: {Path}", suggestion.SourcePath);
                suggestion.SourcePath = "";
                suggestion.ArrayPath = pickedArrayPath;
                return;
            }

            if (!string.IsNullOrWhiteSpace(suggestion.ArrayPath)
                && !SubCardPathHelper.PathsEqual(suggestion.ArrayPath, pickedArrayPath))
            {
                _logger.LogWarning("LLM 子卡 ArrayPath 修正: {Field} 从 {Wrong} 改为 {Correct}", suggestion.TargetField, suggestion.ArrayPath, pickedArrayPath);
            }

            suggestion.ArrayPath = pickedArrayPath;
            suggestion.SourcePath = relativePath;
            return;
        }

        if (!string.IsNullOrWhiteSpace(suggestion.ArrayPath)
            && TryNormalizeSubCardSourceByArrayPath(sampleJson, suggestion, mainRecordArrayPath))
        {
            return;
        }
    }

    private bool TryNormalizeSubCardSourceByArrayPath(JToken sampleJson, MappingSuggestion suggestion, string? mainRecordArrayPath)
    {
        var effectiveArrayPath = SubCardPathHelper.ExpandArrayPathToRoot(sampleJson, suggestion.ArrayPath, mainRecordArrayPath);
        return TryApplySubCardRelativePath(suggestion, effectiveArrayPath)
            || TryApplySubCardRelativePath(suggestion, suggestion.ArrayPath);
    }

    private bool TryApplySubCardRelativePath(MappingSuggestion suggestion, string? arrayPath)
    {
        if (string.IsNullOrWhiteSpace(arrayPath)
            || !SubCardPathHelper.TryBuildRelativePath(suggestion.SourcePath, arrayPath, out var normalizedRelativePath))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(normalizedRelativePath))
        {
            _logger.LogWarning("LLM 子卡建议缺少容器内字段: {Path}", suggestion.SourcePath);
            suggestion.SourcePath = "";
            return true;
        }

        suggestion.SourcePath = normalizedRelativePath;
        return true;
    }

    private static bool TryPreferContextualSubCardSourcePath(
        JToken sampleJson,
        MappingSuggestion suggestion,
        TargetFieldInfo targetField)
    {
        var cardTokens = ExtractTokens(targetField.CardName)
            .Where(static token => token.Length > 1)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (cardTokens.Count == 0 || string.IsNullOrWhiteSpace(suggestion.SourcePath))
        {
            return false;
        }

        var currentPathTokens = ExtractPathTokens(suggestion.SourcePath);
        if (currentPathTokens.Overlaps(cardTokens))
        {
            return false;
        }

        var targetTokens = ExtractFieldTokens(targetField);
        var expandedTargetTokens = ExpandEquivalentTokens(targetTokens);
        var currentScore = ComputeTokenMatchScore(targetTokens, expandedTargetTokens, currentPathTokens);

        var best = GetDistinctLeafHints(sampleJson)
            .Select(leaf => new
            {
                Leaf = leaf,
                PathTokens = ExtractPathTokens(leaf.Path)
            })
            .Where(x => x.PathTokens.Overlaps(cardTokens))
            .Select(x => new
            {
                x.Leaf,
                Score = ComputeTokenMatchScore(targetTokens, expandedTargetTokens, x.PathTokens)
            })
            .Where(x => x.Score > currentScore)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Leaf.Path.Length)
            .FirstOrDefault();

        if (best == null)
        {
            return false;
        }

        suggestion.SourcePath = best.Leaf.Path;
        return true;
    }

    private static string? NormalizeSuggestionArrayPath(string? arrayPath, string? mainRecordArrayPath)
    {
        var normalized = SubCardPathHelper.NormalizeArrayContainerPath(arrayPath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var mainRecordPath = SubCardPathHelper.NormalizeArrayContainerPath(mainRecordArrayPath);
        if (string.IsNullOrWhiteSpace(mainRecordPath))
        {
            return normalized;
        }

        if (SubCardPathHelper.IsMainRecordContainerPath(normalized))
        {
            return SubCardPathHelper.MainRecordContainerPath;
        }

        if (SubCardPathHelper.IsRootContainerPath(normalized))
        {
            return SubCardPathHelper.RootContainerPath;
        }

        if (SubCardPathHelper.PathsEqual(normalized, mainRecordPath))
        {
            return SubCardPathHelper.MainRecordContainerPath;
        }

        return SubCardPathHelper.TryBuildRelativePath(normalized, mainRecordPath, out var relativePath)
               && !string.IsNullOrWhiteSpace(relativePath)
            ? SubCardPathHelper.NormalizeArrayContainerPath(relativePath)
            : normalized;
    }

    private static bool TryInferSubCardContainerPath(
        JToken sampleJson,
        string? sourcePath,
        string? mainRecordArrayPath,
        bool allowMainRecordRoot,
        out string arrayPath,
        out string relativePath)
    {
        arrayPath = "";
        relativePath = "";

        var normalizedSource = SubCardPathHelper.NormalizeArrayPath(sourcePath);
        if (string.IsNullOrWhiteSpace(normalizedSource)
            || SubCardPathHelper.IsAbsoluteJsonPath(normalizedSource)
            || MessageJsonHelper.IsMainRecordScopedPath(normalizedSource))
        {
            return false;
        }

        var mainRecordPath = SubCardPathHelper.NormalizeArrayContainerPath(mainRecordArrayPath);
        var mainContext = MessageJsonHelper.ResolveMainRecordContext(sampleJson, mainRecordPath);
        var isMainRelativePath = false;
        if (!string.IsNullOrWhiteSpace(mainRecordPath)
            && SubCardPathHelper.TryBuildRelativePath(normalizedSource, mainRecordPath, out var mainRelativePath)
            && !string.IsNullOrWhiteSpace(mainRelativePath))
        {
            isMainRelativePath = true;
            if (SubCardPathHelper.TrySplitWildcardPath(mainRelativePath, out var nestedArrayPath, out var nestedRelativePath)
                && !string.IsNullOrWhiteSpace(nestedRelativePath))
            {
                arrayPath = NormalizeSuggestionArrayPath(nestedArrayPath, mainRecordPath) ?? nestedArrayPath;
                relativePath = nestedRelativePath;
                return true;
            }

            if (SubCardPathHelper.TryInferObjectContainerPath(mainContext, mainRelativePath, out var objectPath, out var objectRelativePath))
            {
                arrayPath = NormalizeSuggestionArrayPath(objectPath, mainRecordPath) ?? objectPath;
                relativePath = objectRelativePath;
                return true;
            }

            if (allowMainRecordRoot
                && SubCardPathHelper.ResolveFirstToken(mainContext, mainRelativePath) != null)
            {
                arrayPath = SubCardPathHelper.MainRecordContainerPath;
                relativePath = mainRelativePath;
                return true;
            }
        }

        if (isMainRelativePath)
        {
            return false;
        }

        if (SubCardPathHelper.TrySplitWildcardPath(normalizedSource, out var parsedArrayPath, out var parsedRelativePath)
            && !string.IsNullOrWhiteSpace(parsedRelativePath))
        {
            arrayPath = NormalizeSuggestionArrayPath(parsedArrayPath, mainRecordPath) ?? parsedArrayPath;
            relativePath = parsedRelativePath;
            return true;
        }

        if (SubCardPathHelper.TryInferObjectContainerPath(sampleJson, normalizedSource, out var rootObjectPath, out var rootObjectRelativePath))
        {
            arrayPath = NormalizeSuggestionArrayPath(rootObjectPath, mainRecordPath) ?? rootObjectPath;
            relativePath = rootObjectRelativePath;
            return true;
        }

        if (allowMainRecordRoot
            && !string.IsNullOrWhiteSpace(mainRecordPath)
            && SubCardPathHelper.ResolveFirstToken(mainContext, normalizedSource) != null)
        {
            arrayPath = SubCardPathHelper.MainRecordContainerPath;
            relativePath = normalizedSource;
            return true;
        }

        return false;
    }

    private List<MappingSuggestion> FilterSuggestionsByTargetFields(List<MappingSuggestion> suggestions, List<TargetFieldInfo> targetFields)
    {
        var allowedKeys = targetFields
            .Select(f => GetTargetFieldKey(f.Category, f.FieldId, f.CardId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var filtered = suggestions
            .Where(s => allowedKeys.Contains(GetTargetFieldKey(s.MappingTarget, s.TargetField, s.CardId)))
            .ToList();

        var filteredCount = suggestions.Count - filtered.Count;
        if (filteredCount > 0)
        {
            _logger.LogWarning("LLM 越界建议已过滤: {Count}", filteredCount);
        }

        return filtered;
    }

    private static TargetFieldInfo? ResolveTargetFieldCandidate(MappingSuggestion suggestion, List<TargetFieldInfo> candidates)
    {
        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        if (suggestion.CardId.HasValue)
        {
            var sameCard = candidates.FirstOrDefault(c => c.CardId == suggestion.CardId);
            if (sameCard != null)
            {
                return sameCard;
            }
        }

        var sameCategory = candidates.Where(c => c.Category == suggestion.MappingTarget).ToList();
        if (sameCategory.Count == 1)
        {
            return sameCategory[0];
        }

        var subCardCandidates = candidates.Where(c => c.Category == MappingTarget.SubCard).ToList();
        if (subCardCandidates.Count == 1)
        {
            return subCardCandidates[0];
        }

        return null;
    }

    private static string BuildScopeRuleText(List<TargetFieldInfo> targetFields, string? scopeHint)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(scopeHint))
        {
            lines.Add($"- 界面当前选择范围：{scopeHint}");
        }

        var allowedCategories = targetFields
            .Select(f => f.Category.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (allowedCategories.Count > 0)
        {
            lines.Add($"- 仅允许输出这些 mappingTarget：{string.Join("、", allowedCategories)}");
        }

        if (targetFields.Count == 1)
        {
            var field = targetFields[0];
            var fieldText = $"{field.Category}/{field.FieldId}";
            if (field.CardId.HasValue)
            {
                fieldText += $" [cardId={field.CardId}]";
            }

            lines.Add($"- 当前只允许生成 1 个目标字段：{fieldText}");
        }
        else
        {
            lines.Add($"- 当前只允许输出目标字段列表中的 {targetFields.Count} 个字段，禁止输出列表外字段");
        }

        var subCardNames = targetFields
            .Where(f => f.Category == MappingTarget.SubCard && !string.IsNullOrWhiteSpace(f.CardName))
            .Select(f => f.CardName!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (subCardNames.Count == 1)
        {
            lines.Add($"- 当前子卡范围：{subCardNames[0]}");
        }
        else if (subCardNames.Count > 1)
        {
            lines.Add($"- 当前子卡范围仅限以下子卡：{string.Join("、", subCardNames)}");
        }

        lines.Add("- 如果当前范围内找不到匹配项，请直接返回空数组 []");
        return string.Join("\n", lines);
    }

    private static string BuildMainRecordScopePromptText(string? mainRecordArrayPath)
    {
        var normalizedMainPath = SubCardPathHelper.NormalizeArrayContainerPath(mainRecordArrayPath);
        if (string.IsNullOrWhiteSpace(normalizedMainPath))
        {
            return "";
        }

        return $"""
            ## 主记录路径约束
            已配置主记录路径：{normalizedMainPath}
            - 普通字段优先输出当前主记录内相对路径；如需读取真实根节点字段，sourcePath 必须以 $. 开头。
            - SubCard 的 arrayPath 优先填写当前主记录内的相对容器路径，不要带主记录前缀。
            - 对象型 SubCard 示例：如果源字段是 {normalizedMainPath}[].CommonOrder.TimeField，应输出 arrayPath=CommonOrder，sourcePath=TimeField。
            - 如果当前主记录对象本身就是单行 SubCard 容器，例如 {normalizedMainPath}[].ADMISSION_TIME_ICU，应输出 arrayPath=$main，sourcePath=ADMISSION_TIME_ICU。
            - 当子卡名包含 ICU、手术、诊断等限定语时，优先选择源路径中也带相同限定语的字段，例如 ICU 入出时间应优先选择 ADMISSION_TIME_ICU / DISCHARGE_TIME_ICU，而不是普通 ADMISSION_TIME / DISCHARGE_TIME。
            - 子卡字段要读取当前主记录字段时，sourcePath 才写 $main.xxx；不要把对象型 SubCard 内部字段写成 $main.对象.字段。
            """;
    }

    private static string FormatTargetFieldForPrompt(TargetFieldInfo field)
    {
        var metadata = new List<string>();
        if (field.CardId.HasValue)
        {
            metadata.Add($"cardId={field.CardId}");
        }

        if (!string.IsNullOrWhiteSpace(field.CardName))
        {
            metadata.Add($"cardName={field.CardName}");
        }

        if (!string.IsNullOrWhiteSpace(field.SemanticHint))
        {
            metadata.Add($"hint={field.SemanticHint}");
        }

        var suffix = metadata.Count == 0 ? "" : $" [{string.Join("; ", metadata)}]";
        return $"- {field.Category}/{field.FieldId}: {field.DisplayName} ({field.DataType}){suffix}";
    }

    private static string GetTargetFieldKey(MappingTarget category, string fieldId, Guid? cardId)
    {
        return category == MappingTarget.SubCard
            ? $"{category}:{cardId}:{fieldId}"
            : $"{category}:{fieldId}";
    }

    private static string BuildTypedArrayFilterHintText(JToken sampleJson)
    {
        var hints = FindTypedArrayFilterHints(sampleJson)
            .Take(MaxTypedArrayHintCount)
            .ToList();

        if (hints.Count == 0)
        {
            return string.Empty;
        }

        var lines = string.Join("\n", hints.Select(h =>
            $"- {h.Path}: \"{TruncateForPrompt(h.SampleValue)}\"（{h.Reason}）"));

        return $"""
            ## 数组过滤候选路径（根据样本结构自动识别，仅供参考）
            {lines}
            """;
    }

    private static List<TypedArrayFilterHint> FindTypedArrayFilterHints(JToken root)
    {
        var hints = new List<TypedArrayFilterHint>();
        CollectTypedArrayFilterHints(root, "", 0, hints);

        return hints
            .Where(h => !string.IsNullOrWhiteSpace(h.Path))
            .GroupBy(h => h.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.Confidence).First())
            .OrderByDescending(h => h.Confidence)
            .ThenBy(h => h.Path.Length)
            .ToList();
    }

    private static void CollectTypedArrayFilterHints(
        JToken token,
        string path,
        int depth,
        List<TypedArrayFilterHint> hints)
    {
        if (depth > MaxJsonDepth + 1)
        {
            return;
        }

        switch (token)
        {
            case JObject obj:
                foreach (var prop in obj.Properties())
                {
                    var currentPath = string.IsNullOrEmpty(path) ? prop.Name : $"{path}.{prop.Name}";
                    CollectTypedArrayFilterHints(prop.Value, currentPath, depth + 1, hints);
                }
                break;

            case JArray arr:
                hints.AddRange(BuildTypedArrayFilterHints(arr, path));

                var sampleCount = Math.Min(arr.Count, MaxArraySampleCount);
                for (var i = 0; i < sampleCount; i++)
                {
                    CollectTypedArrayFilterHints(arr[i], $"{path}[]", depth + 1, hints);
                }
                break;
        }
    }

    private static IEnumerable<TypedArrayFilterHint> BuildTypedArrayFilterHints(JArray array, string arrayPath)
    {
        if (string.IsNullOrWhiteSpace(arrayPath) || array.Count == 0)
        {
            yield break;
        }

        var objects = array
            .OfType<JObject>()
            .Take(10)
            .ToList();

        if (objects.Count == 0)
        {
            yield break;
        }

        var typeProperty = TypePropertyCandidates
            .FirstOrDefault(candidate => objects.Any(obj => TryGetScalarProperty(obj, candidate, out _)));
        if (typeProperty == null)
        {
            yield break;
        }

        var valueProperties = ValuePropertyCandidates
            .Where(candidate => objects.Any(obj => TryGetScalarProperty(obj, candidate, out _)))
            .ToList();
        if (valueProperties.Count == 0)
        {
            yield break;
        }

        foreach (var obj in objects)
        {
            if (!TryGetScalarProperty(obj, typeProperty, out var typeToken) || typeToken == null)
            {
                continue;
            }

            var typeValue = typeToken.ToString();
            if (string.IsNullOrWhiteSpace(typeValue))
            {
                continue;
            }

            foreach (var valueProperty in valueProperties)
            {
                if (!TryGetScalarProperty(obj, valueProperty, out var valueToken) || valueToken == null)
                {
                    continue;
                }

                var value = valueToken.ToString();
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                yield return new TypedArrayFilterHint(
                    $"{arrayPath}[?(@.{typeProperty}=='{EscapeJsonPathLiteral(typeValue)}')].{valueProperty}",
                    $"数组元素可按 {typeProperty}={typeValue} 过滤并提取 {valueProperty}",
                    value,
                    0.92);
                break;
            }
        }
    }

    private static bool TryGetScalarProperty(JObject obj, string propertyName, out JToken? token)
    {
        token = obj.Property(propertyName, StringComparison.OrdinalIgnoreCase)?.Value;
        return token != null && token.Type is not JTokenType.Object and not JTokenType.Array and not JTokenType.Null;
    }

    private static (JToken Token, string? Note) PrepareSingleRecordAnalysisToken(JToken token)
    {
        if (token is not JArray array)
        {
            return (token, null);
        }

        if (array.Count == 0)
        {
            return (token, "样本根节点是空数组，无法展开首项结构。");
        }

        return (array[0], $"样本根节点是数组，共 {array.Count} 项；以下结构按首项展开，输出路径请以单条记录为基准，不要带开头的 []。如果当前目标是 SubCard，且每个数组元素就是一行子卡数据，则 arrayPath 填 \"$\"，sourcePath 只填写当前数组元素内的相对字段路径，例如 ADMISSION_TIME_ICU，不要输出 $[] 或 []. 前缀。");
    }

    private static bool PathExists(JToken root, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var normalizedPath = SubCardPathHelper.NormalizeArrayPath(path);
            return SubCardPathHelper.HasArrayWildcard(normalizedPath)
                ? PathExistsWithWildcard(root, normalizedPath)
                : root.SelectToken(normalizedPath) != null;
        }
        catch
        {
            return false;
        }
    }

    private static bool PathExistsWithWildcard(JToken root, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (path.StartsWith("[]", StringComparison.Ordinal))
        {
            if (root is not JArray directArray || directArray.Count == 0)
            {
                return false;
            }

            var remainder = path[2..].TrimStart('.');
            return string.IsNullOrWhiteSpace(remainder)
                || directArray.Any(item => PathExistsWithWildcard(item, remainder));
        }

        if (!SubCardPathHelper.TrySplitWildcardPath(path, out var arrayPath, out var relativePath))
        {
            return SafeSelectToken(root, path) != null;
        }

        var arrayToken = string.IsNullOrWhiteSpace(arrayPath)
            ? root
            : SafeSelectToken(root, arrayPath);
        if (arrayToken is not JArray array || array.Count == 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return true;
        }

        return array.Any(item => PathExistsWithWildcard(item, relativePath));
    }

    private static JToken? SafeSelectToken(JToken token, string path)
    {
        try
        {
            return token.SelectToken(path);
        }
        catch
        {
            return null;
        }
    }

    private static string EscapeJsonPathLiteral(string value) =>
        value.Replace("\\", "\\\\").Replace("'", "\\'");

    private static string TruncateForPrompt(string value) =>
        value.Length > 50 ? value[..50] + "..." : value;

    private sealed record TypedArrayFilterHint(
        string Path,
        string Reason,
        string SampleValue,
        double Confidence);

    private sealed record CodeCandidateHint(
        string Path,
        string Value);

    private sealed record MainRecordArrayHint(
        string Path,
        int Count,
        string SampleKeys);

    private sealed record LeafHint(
        string Path,
        string SampleValue,
        string ValueType,
        int Priority);

    /// <summary>
    /// 解析 LLM 返回的映射建议
    /// </summary>
    private List<MappingSuggestion> ParseSuggestions(string responseText)
    {
        try
        {
            // 尝试提取 JSON 数组
            var startIndex = responseText.IndexOf('[');
            var endIndex = responseText.LastIndexOf(']');
            if (startIndex < 0 || endIndex < 0)
            {
                _logger.LogWarning("LLM 返回内容中未找到 JSON 数组。内容: {Text}", responseText.Length > 300 ? responseText[..300] : responseText);
                return [];
            }

            var jsonArray = responseText[startIndex..(endIndex + 1)];
            using var doc = JsonDocument.Parse(jsonArray);

            var suggestions = new List<MappingSuggestion>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var suggestion = new MappingSuggestion
                {
                    SourcePath = item.TryGetProperty("sourcePath", out var sp) ? sp.GetString() ?? "" : "",
                    TargetField = item.TryGetProperty("targetField", out var tf) ? tf.GetString() ?? "" : "",
                    Confidence = item.TryGetProperty("confidence", out var c) ? c.GetDouble() : 0,
                    Reason = item.TryGetProperty("reason", out var r) ? r.GetString() : null,
                    DictCode = item.TryGetProperty("dictCode", out var dc) ? dc.GetString() : null,
                    DefaultValue = item.TryGetProperty("defaultValue", out var dv) ? dv.GetString() : null,
                    CardId = item.TryGetProperty("cardId", out var ci) ? (Guid.TryParse(ci.GetString(), out var cid) ? cid : null) : null,
                    ArrayPath = item.TryGetProperty("arrayPath", out var ap) ? ap.GetString() : null,
                };

                // 解析 MappingTarget
                if (item.TryGetProperty("mappingTarget", out var mt))
                {
                    var mtStr = mt.GetString() ?? "";
                    suggestion.MappingTarget = mtStr.ToLower() switch
                    {
                        "patient" => Models.Enums.MappingTarget.Patient,
                        "event" => Models.Enums.MappingTarget.Event,
                        "question" => Models.Enums.MappingTarget.Question,
                        "subcard" => Models.Enums.MappingTarget.SubCard,
                        _ => Models.Enums.MappingTarget.Patient,
                    };
                }

                suggestions.Add(suggestion);
            }

            return suggestions;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "解析 LLM 返回结果失败");
            return [];
        }
    }
}
