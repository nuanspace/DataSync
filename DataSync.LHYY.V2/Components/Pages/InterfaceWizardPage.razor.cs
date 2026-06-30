using DataSync.LHYY.V2.Models.Dto;
using DataSync.LHYY.V2.Models.Entities;
using DataSync.LHYY.V2.Models.Enums;
using DataSync.LHYY.V2.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using MudBlazor;
using Newtonsoft.Json.Linq;

namespace DataSync.LHYY.V2.Components.Pages;

public partial class InterfaceWizardPage
{
    [Inject] private BioCoreIntegrationService BioCoreService { get; set; } = default!;
    [Inject] private LlmService LlmService { get; set; } = default!;
    [Inject] private ConfigService ConfigSvc { get; set; } = default!;
    [Inject] private InterfaceRecognitionService InterfaceRecognitionService { get; set; } = default!;
    [Inject] private IdempotentKeyService IdempotentKeyService { get; set; } = default!;
    [Inject] private MessageQueryService MessageQuerySvc { get; set; } = default!;

    [SupplyParameterFromQuery(Name = "tranCode")]
    public string? QueryTranCode { get; set; }
    [SupplyParameterFromQuery(Name = "messageId")]
    public long? QueryMessageId { get; set; }
    private static readonly List<TargetFieldDescriptor> EventTargetFields =
    [
        new() { FieldId = "event_start_time", DisplayName = "事件开始时间", MappingTarget = MappingTarget.Event, DataType = "DateTime" },
        new() { FieldId = "event_end_time", DisplayName = "事件结束时间", MappingTarget = MappingTarget.Event, DataType = "DateTime" },
    ];

    private List<EsbInterfaceConfig> _allInterfaces = [];
    private EsbInterfaceConfig? _selectedConfig;
    private int? _selectedConfigId;
    private JToken? _parsedJson;
    private string _editableJson = "";
    private string? _jsonValidationError;
    private bool _jsonEditorExpanded;
    private bool _projectDocumentsVisible;

    private int _existingMappingCount;
    private List<EsbFieldMapping> _existingMappings = [];
    private List<WizardMappingRow> _mappingRows = [];
    private List<EsbFilterRule> _filterRules = [];
    private int _existingInterfaceRuleCount;
    private int _existingSubCardFilterRuleCount;

    private MappingTarget? _selectedGenerateTarget;
    private string? _selectedEventType;
    private string? _selectedFormId;
    private Guid? _selectedTreeCardId;
    private string? _selectedTreeCardName;
    private List<(string EventType, List<FormNode> Forms)> _formTreeTabs = [];
    private string? _formTreeError;
    private bool _cardsLoading;

    private string _targetSearchText = "";
    private bool _showOnlyUnmappedTargets;
    private bool _autoAdvanceAfterPathPick = true;
    private bool _isRebuildMode = true;
    private bool _saving;

    private PathPickMode _pathPickMode = PathPickMode.None;
    private bool _advanceAfterCurrentPick;
    private string? _activeMappingKey;

    private bool _llmLoading;
    private string? _llmError;
    private List<LlmSuggestionItem> _llmSuggestions = [];
    private bool _llmDialogVisible;
    private bool _previewDialogVisible;
    private LlmSuggestionMode _llmSuggestionMode = LlmSuggestionMode.Scope;
    private string? _llmFocusedMappingKey;
    private string? _llmFocusedFieldName;
    private bool _suggestionPanelExpanded;

    private bool _editDialogVisible;
    private EsbFieldMapping? _editDialogItem;
    private List<EsbFilterRule> _editDialogFilterRules = [];
    private int _editDialogRowIndex = -1;
    private string? _editDialogCleanupMappingKey;

    private bool _settingsDialogVisible;
    private bool _interfaceFilterDialogVisible;
    private bool _saveJsonConfirmVisible;
    private string? _defaultLicenseCode;
    private bool _sampleAnalysisLoading;
    private string? _sampleAnalysisError;
    private List<string> _sampleMatchedTranCodes = [];
    private bool _sampleSelectedInterfaceMatched;
    private string? _sampleIdempotentKey;
    private string? _sampleSourceMessageId;

    private readonly Dictionary<string, QuestionInfo> _questionLookup = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, string> _cardNameLookup = [];
    private readonly Dictionary<Guid, string?> _subCardArrayPathOverrides = [];
    private readonly Dictionary<Guid, List<EsbFilterRule>> _subCardFilterRulesByCardId = [];
    private readonly HashSet<Guid> _expandedSubCardFilterCards = [];
    private QuestionTreeNodeType? _selectedQuestionNodeType;
    private string? _selectedQuestionTreeNodeKey;
    private string? _pathPickTargetKey;
    private PathPickTargetKind _pathPickTargetKind = PathPickTargetKind.SourcePath;
    private int _pathPickRuleIndex = -1;
    private Guid? _pathPickArrayCardId;
    private string? _selectedJsonTreePath;
    private int _selectedJsonTreeVersion;
    private TargetFieldDescriptor? _selectedQuestionField;

    private string? CurrentLicenseCode =>
        string.IsNullOrWhiteSpace(_selectedConfig?.LicenseCode) ? _defaultLicenseCode : _selectedConfig.LicenseCode;

    private enum PathPickMode
    {
        None,
        ActiveRow
    }

    private enum PathPickTargetKind
    {
        SourcePath,
        FilterRule,
        SubCardFilterRule,
        ArrayPath,
    }

    private enum LlmSuggestionMode
    {
        Scope,
        SingleField
    }

    private enum QuestionTreeNodeType
    {
        Card,
        SubCard,
        Question,
    }

    private enum TargetFieldState
    {
        Unmapped,
        Pending,
        Mapped,
        Invalid,
        Disabled,
    }

    public class TargetFieldDescriptor
    {
        public string FieldId { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string? DataType { get; set; }
        public string? SemanticHint { get; set; }
        public string? SelectInfo { get; set; }
        public List<string> Options { get; set; } = [];
        public MappingTarget MappingTarget { get; set; }
        public Guid? CardId { get; set; }
        public string? CardName { get; set; }
        public Guid? ScopeCardId { get; set; }
        public string? ScopeCardName { get; set; }
    }

    public class LlmSuggestionItem
    {
        public string SourcePath { get; set; } = "";
        public string TargetField { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string? Reason { get; set; }
        public string? DictCode { get; set; }
        public string? DefaultValue { get; set; }
        public string? CardName { get; set; }
        public string? ArrayPath { get; set; }
        public MappingTarget MappingTarget { get; set; }
        public double Confidence { get; set; }
        public Guid? CardId { get; set; }
        public bool IsSelected { get; set; } = true;
    }

    private class WizardMappingRow
    {
        public MappingTarget MappingTarget { get; set; }
        public string SourcePath { get; set; } = "";
        public string TargetField { get; set; } = "";
        public string TargetFieldDisplayName { get; set; } = "";
        public string? DictCode { get; set; }
        public string DictMatchMode { get; set; } = EsbFieldMapping.DefaultDictMatchMode;
        public string? DefaultValue { get; set; }
        public string? ValueExpression { get; set; }
        public string? CardName { get; set; }
        public string? ArrayPath { get; set; }
        public Guid? CardId { get; set; }
        public bool IsRequired { get; set; }
        public bool IsEnabled { get; set; } = true;
        public List<EsbFilterRule> FilterRules { get; set; } = [];
        public string Origin { get; set; } = "manual";
        public bool IsPathValid { get; set; } = true;
        public int ExistingId { get; set; }
    }

    private sealed class SuggestionGroupViewModel
    {
        public string Key { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Subtitle { get; set; }
        public Guid? CardId { get; set; }
        public string? ArrayPath { get; set; }
        public bool IsSubCardGroup { get; set; }
        public bool HasArrayPathConflict { get; set; }
        public bool HasMissingArrayPath { get; set; }
        public Color Color { get; set; } = Color.Default;
        public int SortOrder { get; set; }
        public List<LlmSuggestionItem> Items { get; set; } = [];
    }

    private readonly record struct NodeStatusSummary(int Total, int Mapped, int Pending, int Invalid, int Disabled)
    {
        public int Unmapped => Math.Max(0, Total - Mapped - Pending - Invalid - Disabled);
    }

    private WizardMappingRow? ActiveRow =>
        string.IsNullOrEmpty(_activeMappingKey)
            ? null
            : _mappingRows.FirstOrDefault(r => GetMappingKey(r) == _activeMappingKey);

    private TargetFieldDescriptor? SelectedStandaloneField =>
        IsQuestionWorkbenchMode ? null : FindCurrentTargetField(_activeMappingKey);

    private TargetFieldDescriptor? SelectedQuestionField =>
        _selectedGenerateTarget is MappingTarget.Question or MappingTarget.SubCard
        && _selectedQuestionNodeType == QuestionTreeNodeType.Question
            ? _selectedQuestionField
            : null;

    private WizardMappingRow? SelectedQuestionRow =>
        SelectedQuestionField == null ? null : FindMappingRow(SelectedQuestionField);

    private FormNode? ActiveForm =>
        ActiveFormOptions.FirstOrDefault(f => f.Id == _selectedFormId) ?? ActiveFormOptions.FirstOrDefault();

    private List<FormNode> ActiveFormOptions =>
        _formTreeTabs.FirstOrDefault(t => t.EventType == _selectedEventType).Forms ?? [];

    private CardNode? SelectedScopeCard =>
        _selectedGenerateTarget == MappingTarget.Question
        && ActiveForm != null
        && _selectedTreeCardId.HasValue
            ? FindCardNode(ActiveForm.Cards, _selectedTreeCardId.Value)
            : null;

    private CardNode? CurrentSubCardGroupCard => GetCurrentSubCardGroupCard();

    private bool IsCurrentSubCardGroupMode => CurrentSubCardGroupCard != null;

    private List<TargetFieldDescriptor> VisibleTargetFields => GetCurrentTargetFields(applySearchFilters: true);

    private bool RequiresCardSelection =>
        _selectedGenerateTarget is MappingTarget.Question or MappingTarget.SubCard;

    private bool IsQuestionWorkbenchMode =>
        _selectedGenerateTarget is MappingTarget.Question or MappingTarget.SubCard;

    private bool HasQuestionScopeSelection => SelectedScopeCard != null;

    private bool CanRunLlmSuggest => !_llmLoading
        && _parsedJson != null
        && _selectedGenerateTarget != null
        && (!RequiresCardSelection || HasQuestionScopeSelection);

    private bool CanRunActiveFieldLlmSuggest => !_llmLoading
        && _parsedJson != null
        && _selectedConfig != null
        && (ActiveRow != null || SelectedQuestionField != null || SelectedStandaloneField != null);

    private bool AllSuggestionsSelected => _llmSuggestions.Any() && _llmSuggestions.All(s => s.IsSelected);

    private int SelectedSuggestionCount => _llmSuggestions.Count(s => s.IsSelected);

    private bool CanApplyCurrentSuggestions => CanApplySuggestions(_llmSuggestions.Where(s => s.IsSelected));

    private bool IsSingleFieldSuggestionMode =>
        _llmSuggestionMode == LlmSuggestionMode.SingleField && !string.IsNullOrWhiteSpace(_llmFocusedFieldName);

    private static bool HasValueSource(string? sourcePath, string? defaultValue) =>
        !string.IsNullOrWhiteSpace(sourcePath) || !string.IsNullOrWhiteSpace(defaultValue);

    private static bool HasValueSource(WizardMappingRow row) => HasValueSource(row.SourcePath, row.DefaultValue);

    private static bool IsAbsoluteJsonPath(string? path) =>
        SubCardPathHelper.IsAbsoluteJsonPath(path);

    private static bool IsMainRecordScopedPath(string? path) =>
        MessageJsonHelper.IsMainRecordScopedPath(path);

    private static string TrimJsonRootPrefix(string? path) =>
        SubCardPathHelper.TrimJsonRootPrefix(path);

    private static string TrimMainRecordScopePrefix(string? path) =>
        MessageJsonHelper.TrimMainRecordScopePrefix(path);

    private string GetDisplaySourcePath(MappingTarget mappingTarget, Guid? cardId, string? sourcePath, string? arrayPath)
    {
        var normalizedSource = sourcePath?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(normalizedSource))
        {
            return "";
        }

        if (mappingTarget != MappingTarget.SubCard)
        {
            return TryNormalizeMainRecordRelativeSourcePath(normalizedSource, out var standaloneMainRecordRelativePath)
                ? standaloneMainRecordRelativePath
                : normalizedSource;
        }

        if (SubCardPathHelper.IsMainRecordContainerPath(arrayPath))
        {
            var mainRecordPath = GetMainRecordArrayPath();
            return !string.IsNullOrWhiteSpace(mainRecordPath)
                   && SubCardPathHelper.TryBuildRelativePath(normalizedSource, mainRecordPath, out var mainRelativePath)
                   && !string.IsNullOrWhiteSpace(mainRelativePath)
                ? mainRelativePath
                : normalizedSource;
        }

        if (TryBuildSubCardRelativeSourcePath(normalizedSource, cardId, arrayPath, out var relativePath))
        {
            normalizedSource = relativePath;
        }

        var effectiveArrayPath = GetEffectiveArrayPath(cardId, arrayPath);
        if (TryNormalizeMainRecordSourcePath(normalizedSource, out var subCardMainRecordScopedPath)
            && !SubCardPathHelper.HasArrayWildcard(TrimMainRecordScopePrefix(subCardMainRecordScopedPath)))
        {
            return subCardMainRecordScopedPath;
        }

        if (string.IsNullOrWhiteSpace(effectiveArrayPath)
            || SubCardPathHelper.HasArrayWildcard(normalizedSource)
            || SubCardPathHelper.IsRootScopedPath(normalizedSource, effectiveArrayPath))
        {
            return normalizedSource;
        }

        return normalizedSource;
    }

    private string GetDisplaySourcePath(WizardMappingRow row) =>
        GetDisplaySourcePath(row.MappingTarget, row.CardId, row.SourcePath, row.ArrayPath);

    private bool TryBuildSubCardRelativeSourcePath(
        string? sourcePath,
        Guid? cardId,
        string? arrayPath,
        out string relativePath)
    {
        relativePath = "";
        var normalizedSource = SubCardPathHelper.NormalizeArrayPath(sourcePath);
        if (string.IsNullOrWhiteSpace(normalizedSource))
        {
            return false;
        }

        var candidatePaths = new List<string?>();
        var effectiveArrayPath = GetEffectiveArrayPath(cardId, arrayPath);
        candidatePaths.Add(effectiveArrayPath);
        candidatePaths.Add(NormalizeEditableSubCardArrayPathValue(arrayPath));

        if (cardId.HasValue)
        {
            var cardArrayPath = GetSubCardArrayPath(cardId.Value);
            candidatePaths.Add(ExpandSubCardArrayPathToRoot(cardArrayPath));
            candidatePaths.Add(NormalizeEditableSubCardArrayPathValue(cardArrayPath));
        }

        foreach (var candidatePath in candidatePaths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (SubCardPathHelper.TryBuildRelativePath(normalizedSource, candidatePath, out var candidateRelativePath)
                && !string.IsNullOrWhiteSpace(candidateRelativePath))
            {
                relativePath = candidateRelativePath;
                return true;
            }
        }

        return false;
    }

    private static string? NormalizeSubCardArrayPathValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = SubCardPathHelper.NormalizeArrayContainerPath(value);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private string? GetMainRecordArrayPath() =>
        NormalizeSubCardArrayPathValue(_selectedConfig?.MainRecordArrayPath);

    private JToken? GetConfiguredMainRecordContext()
    {
        var mainRecordArrayPath = GetMainRecordArrayPath();
        if (_parsedJson == null || string.IsNullOrWhiteSpace(mainRecordArrayPath))
        {
            return null;
        }

        return SubCardPathHelper.ResolveFirstSubCardContext(_parsedJson, mainRecordArrayPath);
    }

    private bool TryBuildMainRecordScopedPath(string? sourcePath, out string scopedPath)
    {
        scopedPath = "";
        var normalizedSource = sourcePath?.Trim() ?? "";
        var mainRecordArrayPath = GetMainRecordArrayPath();
        var mainRecordContext = GetConfiguredMainRecordContext();
        if (string.IsNullOrWhiteSpace(normalizedSource)
            || string.IsNullOrWhiteSpace(mainRecordArrayPath)
            || mainRecordContext == null
            || SubCardPathHelper.IsAbsoluteJsonPath(normalizedSource)
            || SubCardPathHelper.IsRootScopedPath(normalizedSource, mainRecordArrayPath))
        {
            return false;
        }

        if (IsMainRecordScopedPath(normalizedSource))
        {
            var relativePath = TrimMainRecordScopePrefix(normalizedSource);
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                return false;
            }

            scopedPath = MessageJsonHelper.EnsureMainRecordScopedPath(relativePath);
            return true;
        }

        if (SubCardPathHelper.ResolveFirstToken(mainRecordContext, normalizedSource) == null)
        {
            return false;
        }

        scopedPath = MessageJsonHelper.EnsureMainRecordScopedPath(normalizedSource);
        return true;
    }

    private bool TryNormalizeMainRecordSourcePath(string? sourcePath, out string normalizedPath)
        => MessageJsonHelper.TryNormalizeMainRecordSourcePath(sourcePath, GetMainRecordArrayPath(), out normalizedPath);

    private bool TryNormalizeMainRecordRelativeSourcePath(string? sourcePath, out string normalizedPath)
        => MessageJsonHelper.TryNormalizeMainRecordRelativeSourcePath(sourcePath, GetMainRecordArrayPath(), out normalizedPath);

    private string NormalizeSubCardSourcePathForPreview(string? sourcePath)
    {
        var normalizedSourcePath = sourcePath?.Trim() ?? "";
        return TryNormalizeMainRecordSourcePath(normalizedSourcePath, out var mainRecordScopedPath)
               && !SubCardPathHelper.HasArrayWildcard(TrimMainRecordScopePrefix(mainRecordScopedPath))
            ? mainRecordScopedPath
            : normalizedSourcePath;
    }

    private JToken? ResolveSubCardScopedPreviewToken(Guid? cardId, string? arrayPath, string? sourcePath, out bool resolvedFromMainRecord)
    {
        resolvedFromMainRecord = false;
        if (_parsedJson == null || string.IsNullOrWhiteSpace(sourcePath))
        {
            return null;
        }

        if (IsMainRecordScopedPath(sourcePath))
        {
            var mainRecordContext = GetConfiguredMainRecordContext();
            if (mainRecordContext == null)
            {
                return null;
            }

            resolvedFromMainRecord = true;
            return SubCardPathHelper.ResolveFirstToken(mainRecordContext, TrimMainRecordScopePrefix(sourcePath));
        }

        var effectiveArrayPath = GetEffectiveArrayPath(cardId, arrayPath, sourcePath);
        if (!string.IsNullOrWhiteSpace(effectiveArrayPath))
        {
            var itemContext = SubCardPathHelper.ResolveFirstSubCardContext(_parsedJson, effectiveArrayPath);
            var itemToken = itemContext == null ? null : SubCardPathHelper.ResolveFirstToken(itemContext, sourcePath);
            if (itemToken != null)
            {
                return itemToken;
            }

            if (!SubCardPathHelper.HasArrayWildcard(sourcePath))
            {
                return SubCardPathHelper.ResolveFirstToken(_parsedJson, BuildSubCardScopedPath(effectiveArrayPath, sourcePath));
            }
        }

        return null;
    }

    private JToken? ResolveStandalonePreviewToken(string? sourcePath)
    {
        if (_parsedJson == null || string.IsNullOrWhiteSpace(sourcePath))
        {
            return null;
        }

        var mainContext = MessageJsonHelper.ResolveMainRecordContext(_parsedJson, _selectedConfig?.MainRecordArrayPath);
        return MessageJsonHelper.ResolveFirstScopedToken(_parsedJson, mainContext, sourcePath);
    }

    private string GetSourcePathHelperText(MappingTarget mappingTarget)
    {
        if (mappingTarget != MappingTarget.SubCard && !string.IsNullOrWhiteSpace(GetMainRecordArrayPath()))
        {
            return "点击后再去左侧 JSON 树点选；也支持直接输入。已配置主数据路径时，普通字段直接写主记录内相对路径；如需取根级字段请写 $.xxx。";
        }

        if (mappingTarget == MappingTarget.SubCard && !string.IsNullOrWhiteSpace(GetMainRecordArrayPath()))
        {
            return "点击后再去左侧 JSON 树点选；也支持直接输入。子卡字段默认填写数组项内相对路径；如需取当前主记录字段，请写 $main.xxx。";
        }

        return "点击后再去左侧 JSON 树点选；也支持直接输入。只用默认值时可留空。";
    }

    private string GetSubCardArrayPathHelperText()
    {
        return string.IsNullOrWhiteSpace(GetMainRecordArrayPath())
            ? "子卡共用容器路径。字段默认填写数组项内相对路径；如需取根级字段，请以 $. 开头。"
            : "子卡共用容器路径。已配置主记录路径时，这里优先填写主记录内相对路径；如需取根级容器，请以 $. 开头。";
    }

    private string NormalizeSuggestionSourcePath(MappingTarget mappingTarget, Guid? cardId, string? arrayPath, string? sourcePath)
    {
        var normalizedPath = SubCardPathHelper.NormalizeArrayPath(sourcePath);
        return NormalizeEditableSourcePath(mappingTarget, cardId, arrayPath, normalizedPath);
    }

    private bool TryNormalizeMainRecordArrayPath(string? arrayPath, out string normalizedPath)
    {
        normalizedPath = NormalizeSubCardArrayPathValue(arrayPath) ?? "";
        var mainRecordArrayPath = GetMainRecordArrayPath();
        if (string.IsNullOrWhiteSpace(normalizedPath) || string.IsNullOrWhiteSpace(mainRecordArrayPath))
        {
            return false;
        }

        if (SubCardPathHelper.PathsEqual(normalizedPath, mainRecordArrayPath))
        {
            normalizedPath = SubCardPathHelper.MainRecordContainerPath;
            return true;
        }

        if (!SubCardPathHelper.TryBuildRelativePath(normalizedPath, mainRecordArrayPath, out var relativePath)
            || string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        normalizedPath = string.IsNullOrWhiteSpace(relativePath)
            ? SubCardPathHelper.MainRecordContainerPath
            : relativePath;
        return true;
    }

    private string? NormalizeEditableSubCardArrayPathValue(string? value)
    {
        var normalized = NormalizeSubCardArrayPathValue(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return TryNormalizeMainRecordArrayPath(normalized, out var mainRecordRelativePath)
            ? mainRecordRelativePath
            : normalized;
    }

    private string? ExpandSubCardArrayPathToRoot(string? arrayPath)
        => SubCardPathHelper.ExpandArrayPathToRoot(_parsedJson, NormalizeEditableSubCardArrayPathValue(arrayPath), GetMainRecordArrayPath());

    private string NormalizeEditableSourcePath(MappingTarget mappingTarget, Guid? cardId, string? arrayPath, string? value)
    {
        var normalized = value?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return normalized;
        }

        if (mappingTarget != MappingTarget.SubCard)
        {
            return TryNormalizeMainRecordRelativeSourcePath(normalized, out var mainRecordRelativePath)
                ? mainRecordRelativePath
                : normalized;
        }

        if (TryBuildSubCardRelativeSourcePath(normalized, cardId, arrayPath, out var currentRelativePath))
        {
            return currentRelativePath;
        }

        if (TryNormalizeMainRecordSourcePath(normalized, out var subCardMainRecordScopedPath)
            && !SubCardPathHelper.HasArrayWildcard(TrimMainRecordScopePrefix(subCardMainRecordScopedPath)))
        {
            return subCardMainRecordScopedPath;
        }

        if (SubCardPathHelper.IsAbsoluteJsonPath(normalized))
        {
            return normalized;
        }

        if (SubCardPathHelper.HasArrayWildcard(normalized))
        {
            return SubCardPathHelper.NormalizeArrayPath(normalized);
        }

        return UsesRelativeSubCardPath(mappingTarget, cardId, arrayPath, normalized)
            ? normalized
            : SubCardPathHelper.EnsureAbsoluteJsonPath(normalized);
    }

    private int ConfiguredMappingCount =>
        _mappingRows.Count(r => r.IsEnabled && HasValueSource(r));

    private int PendingMappingCount =>
        _mappingRows.Count(r => r.IsEnabled && !HasValueSource(r));

    private int InvalidMappingCount =>
        _mappingRows.Count(r => r.IsEnabled && !string.IsNullOrWhiteSpace(r.SourcePath) && !r.IsPathValid);

    private bool HasCurrentWorkbenchContent =>
        BuildMappingsForSave().Count > 0
        || _filterRules.Count > 0
        || _subCardFilterRulesByCardId.Values.Any(rules => rules.Count > 0);

    private bool HasPersistedWorkbenchContent =>
        _existingMappingCount > 0 || _existingInterfaceRuleCount > 0 || _existingSubCardFilterRuleCount > 0;

    private bool CanEditInterfaceFilters =>
        _selectedConfig != null && !string.IsNullOrWhiteSpace(_selectedConfig.SampleJson);

    private int EnabledInterfaceFilterCount =>
        _filterRules.Count(r => r.IsEnabled);

    private int DisabledInterfaceFilterCount =>
        _filterRules.Count(r => !r.IsEnabled);

    private bool HasSampleAnalysisPreview =>
        _sampleAnalysisLoading
        || !string.IsNullOrWhiteSpace(_sampleAnalysisError)
        || _sampleMatchedTranCodes.Count > 0
        || !string.IsNullOrWhiteSpace(_sampleIdempotentKey)
        || !string.IsNullOrWhiteSpace(_sampleSourceMessageId);

    private MappingTarget? GetCurrentPathPickMappingTarget()
    {
        if (_pathPickMode != PathPickMode.ActiveRow)
        {
            return ActiveRow?.MappingTarget;
        }

        if (_pathPickTargetKind is PathPickTargetKind.ArrayPath or PathPickTargetKind.SubCardFilterRule)
        {
            return MappingTarget.SubCard;
        }

        if (string.IsNullOrWhiteSpace(_pathPickTargetKey))
        {
            return ActiveRow?.MappingTarget;
        }

        var row = _mappingRows.FirstOrDefault(r =>
            string.Equals(GetMappingKey(r), _pathPickTargetKey, StringComparison.OrdinalIgnoreCase));
        if (row != null)
        {
            return row.MappingTarget;
        }

        var field = FindCurrentTargetField(_pathPickTargetKey);
        return field?.MappingTarget;
    }

    private bool UseArrayBracketsInWorkbenchTree =>
        GetCurrentPathPickMappingTarget() == MappingTarget.SubCard
        || (!string.IsNullOrWhiteSpace(_selectedJsonTreePath)
            && _selectedJsonTreePath.Contains("[]", StringComparison.Ordinal));

    private Variant GetGenerateTargetButtonVariant(MappingTarget target) =>
        _selectedGenerateTarget == target ? Variant.Filled : Variant.Outlined;

    private bool _canSaveWorkbench =>
        !_saving
        && _selectedConfig != null
        && _parsedJson != null
        && (HasCurrentWorkbenchContent || HasPersistedWorkbenchContent);

    protected override async Task OnPageInitializedAsync()
    {
        _defaultLicenseCode = await ConfigSvc.GetDefaultLicenseCodeAsync(CurrentIntegrationProjectCode);

        await using var db = await ContextFactory.CreateDbContextAsync();
        _allInterfaces = await ApplyCurrentProjectScope(db.EsbInterfaceConfigs, false)
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.TranCode)
            .ToListAsync();

        if (QueryMessageId.HasValue)
        {
            await LoadInterfaceDataFromMessageAsync(QueryMessageId.Value);
            return;
        }

        if (!string.IsNullOrEmpty(QueryTranCode))
        {
            var cfg = _allInterfaces.FirstOrDefault(c => c.TranCode == QueryTranCode);
            if (cfg != null)
            {
                _selectedConfigId = cfg.Id;
                await LoadInterfaceData(cfg);
            }
        }
    }

    private async Task LoadInterfaceDataFromMessageAsync(long messageId)
    {
        var message = await MessageQuerySvc.GetMessageByIdAsync(messageId);
        if (message == null)
        {
            inj_snackbar.Add("消息不存在，无法进入接口配置工作台。", Severity.Warning);
            return;
        }

        if (!MessageJsonHelper.TryParseToken(message.RawJson, out var sampleToken, out var error))
        {
            inj_snackbar.Add(error ?? "消息 RawJson 无效，无法进入接口配置工作台。", Severity.Warning);
            return;
        }

        var cfg = _allInterfaces.FirstOrDefault(c =>
            string.Equals(c.TranCode, message.TranCode, StringComparison.OrdinalIgnoreCase));
        if (cfg == null)
        {
            var confirmed = await inj_dialogService.ShowMessageBox(
                "缺少接口配置",
                $"未找到事件代码 {message.TranCode} 对应的接口配置，是否先去创建接口？",
                yesText: "去创建",
                cancelText: "取消");

            if (confirmed == true)
            {
                inj_navigationManager.NavigateTo($"/config/interfaces?createFromMessageId={messageId}");
            }
            return;
        }

        cfg.SampleJson = sampleToken.ToString(Newtonsoft.Json.Formatting.Indented);
        _selectedConfigId = cfg.Id;
        await LoadInterfaceData(cfg);
        inj_snackbar.Add("已使用当前消息 RawJson 作为工作台样例，点击“保存到数据库”才会覆盖接口模板。", Severity.Info);
    }

    private Task<IEnumerable<EsbInterfaceConfig>> SearchInterfaces(string? search, CancellationToken _)
    {
        IEnumerable<EsbInterfaceConfig> items = _allInterfaces;
        if (!string.IsNullOrWhiteSpace(search))
        {
            var keyword = search.Trim();
            items = items.Where(c =>
                c.TranCode.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                || (c.TranName?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        return Task.FromResult(items.Take(80));
    }

    private static string FormatInterfaceOption(EsbInterfaceConfig? cfg) =>
        cfg == null ? "" : $"{cfg.TranCode} - {cfg.TranName}";

    private async Task OnInterfaceSelected(EsbInterfaceConfig? cfg)
    {
        _selectedConfigId = cfg?.Id;
        if (cfg == null)
        {
            _selectedConfig = null;
            _editableJson = "";
            _parsedJson = null;
            _jsonValidationError = null;
            _existingMappings = [];
            _existingMappingCount = 0;
            _filterRules = [];
            _existingInterfaceRuleCount = 0;
            _existingSubCardFilterRuleCount = 0;
            ResetSampleAnalysis();
            ResetWorkbenchState();
            return;
        }

        await LoadInterfaceData(cfg);
    }

    private async Task LoadInterfaceData(EsbInterfaceConfig cfg)
    {
        _selectedConfig = cfg;
        _editableJson = cfg.SampleJson ?? "";
        _jsonValidationError = null;
        _jsonEditorExpanded = string.IsNullOrWhiteSpace(_editableJson);
        ResetWorkbenchState();
        ValidateJson(_editableJson);

        await using var db = await ContextFactory.CreateDbContextAsync();

        var allMappings = await db.EsbFieldMappings
            .Where(m => m.TranCode == cfg.TranCode && m.IntegrationProjectCode == cfg.IntegrationProjectCode)
            .OrderBy(m => m.SortOrder)
            .ToListAsync();

        _existingMappings = allMappings
            .Where(m => !EsbFieldMapping.IsSubCardFilterMapping(m))
            .ToList();

        Dictionary<int, List<EsbFilterRule>> mappingRuleMap = [];
        var mappingIds = allMappings.Select(m => m.Id).ToList();
        if (mappingIds.Count > 0)
        {
            var mappingRules = await db.EsbFilterRules
                .Where(r => r.MappingId != null
                            && mappingIds.Contains(r.MappingId.Value))
                .OrderBy(r => r.RuleGroup)
                .ThenBy(r => r.SortOrder)
                .ToListAsync();

            mappingRuleMap = mappingRules
                .GroupBy(r => r.MappingId!.Value)
                .ToDictionary(g => g.Key, g => g.ToList());
        }

        _filterRules = await db.EsbFilterRules
            .Where(r => r.TranCode == cfg.TranCode && r.MappingId == null && r.IntegrationProjectCode == cfg.IntegrationProjectCode)
            .OrderBy(r => r.RuleGroup)
            .ThenBy(r => r.SortOrder)
            .ToListAsync();

        _existingMappingCount = _existingMappings.Count;
        _existingInterfaceRuleCount = _filterRules.Count;
        _isRebuildMode = _existingMappingCount == 0;

        PreloadSubCardFilterRules(allMappings, mappingRuleMap);
        _existingSubCardFilterRuleCount = _subCardFilterRulesByCardId.Values.Sum(rules => rules.Count);
        PreloadExistingMappings(mappingRuleMap);
    }

    private void ResetWorkbenchState()
    {
        _mappingRows = [];
        _selectedGenerateTarget = null;
        _selectedEventType = null;
        _selectedFormId = null;
        _selectedTreeCardId = null;
        _selectedTreeCardName = null;
        _formTreeTabs = [];
        _formTreeError = null;
        _cardsLoading = false;
        _llmSuggestions = [];
        _llmError = null;
        _llmLoading = false;
        _llmDialogVisible = false;
        _previewDialogVisible = false;
        _llmSuggestionMode = LlmSuggestionMode.Scope;
        _llmFocusedMappingKey = null;
        _llmFocusedFieldName = null;
        _targetSearchText = "";
        _showOnlyUnmappedTargets = false;
        _pathPickMode = PathPickMode.None;
        _advanceAfterCurrentPick = false;
        _activeMappingKey = null;
        _editDialogVisible = false;
        _interfaceFilterDialogVisible = false;
        _saveJsonConfirmVisible = false;
        _suggestionPanelExpanded = false;
        _questionLookup.Clear();
        _cardNameLookup.Clear();
        _subCardArrayPathOverrides.Clear();
        _subCardFilterRulesByCardId.Clear();
        _expandedSubCardFilterCards.Clear();
        _selectedQuestionNodeType = null;
        _selectedQuestionTreeNodeKey = null;
        _pathPickTargetKey = null;
        _pathPickTargetKind = PathPickTargetKind.SourcePath;
        _pathPickRuleIndex = -1;
        _pathPickArrayCardId = null;
        _selectedJsonTreePath = null;
        _selectedJsonTreeVersion = 0;
        _selectedQuestionField = null;
    }

    private Task OnProjectDocumentsVisibleChanged(bool visible)
    {
        _projectDocumentsVisible = visible;
        return Task.CompletedTask;
    }

    private void ResetSampleAnalysis()
    {
        _sampleAnalysisLoading = false;
        _sampleAnalysisError = null;
        _sampleMatchedTranCodes = [];
        _sampleSelectedInterfaceMatched = false;
        _sampleIdempotentKey = null;
        _sampleSourceMessageId = null;
    }

    private string GetInterfaceFilterSummaryText()
    {
        return _filterRules.Count == 0 ? "未配置" : $"共 {_filterRules.Count} 条";
    }

    private string GetSampleMatchedInterfacesSummaryText()
    {
        return _sampleMatchedTranCodes.Count switch
        {
            0 => "当前样本未命中任何接口",
            1 => $"当前样本命中 1 个接口：{_sampleMatchedTranCodes[0]}",
            _ => $"当前样本命中 {_sampleMatchedTranCodes.Count} 个接口：{string.Join("、", _sampleMatchedTranCodes)}"
        };
    }

    private string GetSampleIdempotentKeyText()
        => string.IsNullOrWhiteSpace(_sampleIdempotentKey) ? "未配置或未生成" : _sampleIdempotentKey;

    private string GetSampleSourceMessageIdText()
        => string.IsNullOrWhiteSpace(_sampleSourceMessageId) ? "未提取到" : _sampleSourceMessageId;

    private void OnJsonTextChanged(string value)
    {
        _editableJson = value;
        ValidateJson(value);
    }

    private void ValidateJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            _parsedJson = null;
            _jsonValidationError = null;
            ResetSampleAnalysis();
            return;
        }

        try
        {
            _parsedJson = JToken.Parse(json);
            _jsonValidationError = null;
            foreach (var row in _mappingRows)
            {
                ValidateMappingRowPath(row);
            }
            _ = InvokeAsync(RefreshSampleAnalysisAsync);
        }
        catch (Exception ex)
        {
            _parsedJson = null;
            _jsonValidationError = ex.Message.Length > 80 ? ex.Message[..80] + "..." : ex.Message;
            ResetSampleAnalysis();
        }
    }

    private async Task RefreshSampleAnalysisAsync()
    {
        if (_selectedConfig == null || _parsedJson == null || !string.IsNullOrWhiteSpace(_jsonValidationError))
        {
            ResetSampleAnalysis();
            return;
        }

        _sampleAnalysisLoading = true;
        _sampleAnalysisError = null;
        _sampleMatchedTranCodes = [];
        _sampleSelectedInterfaceMatched = false;
        _sampleIdempotentKey = null;
        _sampleSourceMessageId = null;

        try
        {
            var matches = await InterfaceRecognitionService.ResolveAsync(_parsedJson, false);
            var currentProjectTranCodes = _allInterfaces
                .Select(i => i.TranCode)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _sampleMatchedTranCodes = matches
                .Select(m => m.Config.TranCode)
                .Where(currentProjectTranCodes.Contains)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _sampleSelectedInterfaceMatched = _sampleMatchedTranCodes
                .Contains(_selectedConfig.TranCode, StringComparer.OrdinalIgnoreCase);

            _sampleSourceMessageId = IdempotentKeyService.ResolveSourceMessageId(_parsedJson, _selectedConfig);
            _sampleIdempotentKey = await IdempotentKeyService.BuildIdempotentKeyAsync(_parsedJson, _selectedConfig, false);
        }
        catch (Exception ex)
        {
            _sampleAnalysisError = ex.Message;
        }
        finally
        {
            _sampleAnalysisLoading = false;
            await InvokeAsync(StateHasChanged);
        }
    }

    private void FormatJson()
    {
        if (_parsedJson != null)
        {
            _editableJson = _parsedJson.ToString(Newtonsoft.Json.Formatting.Indented);
        }
    }

    private void ResetJsonToOriginal()
    {
        if (_selectedConfig != null)
        {
            _editableJson = _selectedConfig.SampleJson ?? "";
            ValidateJson(_editableJson);
        }
    }

    private void SaveJsonToDatabase()
    {
        _saveJsonConfirmVisible = true;
    }

    private async Task ConfirmSaveJsonToDatabase()
    {
        _saveJsonConfirmVisible = false;
        if (_selectedConfig == null || _parsedJson == null)
        {
            return;
        }

        try
        {
            await using var db = await ContextFactory.CreateDbContextAsync();
            var entity = await db.EsbInterfaceConfigs.FindAsync(_selectedConfig.Id);
            if (entity != null)
            {
                entity.SampleJson = _editableJson;
                await db.SaveChangesAsync();
                _selectedConfig.SampleJson = _editableJson;
                inj_snackbar.Add("JSON 已保存到数据库", Severity.Success);
            }
        }
        catch (Exception ex)
        {
            inj_snackbar.Add($"保存失败: {ex.Message}", Severity.Error);
        }
    }

    private async Task OnGenerateTargetChanged(MappingTarget? target)
    {
        if (_selectedGenerateTarget == target)
        {
            return;
        }

        _selectedGenerateTarget = target;
        _llmSuggestions = [];
        _llmError = null;
        _activeMappingKey = null;
        ClearPathPickState();
        _targetSearchText = "";

        if (target is MappingTarget.Question or MappingTarget.SubCard)
        {
            if (_formTreeTabs.Count == 0 && !string.IsNullOrEmpty(CurrentLicenseCode))
            {
                await LoadFormTreeAsync();
            }

            _selectedTreeCardId = null;
            _selectedTreeCardName = null;
            _selectedQuestionField = null;
            _selectedQuestionNodeType = null;
            _selectedQuestionTreeNodeKey = null;
        }
        else
        {
            _selectedTreeCardId = null;
            _selectedTreeCardName = null;
            _selectedQuestionField = null;
            _selectedQuestionNodeType = null;
            _selectedQuestionTreeNodeKey = null;
        }
    }

    private async Task LoadFormTreeAsync()
    {
        var licenseCode = CurrentLicenseCode;
        if (string.IsNullOrEmpty(licenseCode))
        {
            _formTreeError = "未配置默认 LicenseCode。";
            return;
        }

        _cardsLoading = true;
        _formTreeError = null;

        try
        {
            var eventTypes = await BioCoreService.GetEventTypesAsync(licenseCode);
            if (eventTypes.Count == 0)
            {
                _formTreeError = $"LicenseCode [{licenseCode}] 未匹配到事件类型，请检查 Bio.Core 配置。";
                _formTreeTabs = [];
                _selectedEventType = null;
                _selectedFormId = null;
                return;
            }

            _formTreeTabs = [];
            foreach (var (eventType, formSetId) in eventTypes)
            {
                var questionDict = await BioCoreService.GetFormQuestionDictByFormSetAsync(formSetId);
                var questions = questionDict.Values
                    .Select(FormBrowserHelper.BuildQuestionInfo)
                    .ToList();
                var cards = await BioCoreService.GetAllCardListByFormSetAsync(formSetId);
                var formInfo = await BioCoreService.GetFormListByFormSetAsync(formSetId);
                var forms = FormBrowserHelper.BuildTree(questions, cards, formInfo);
                _formTreeTabs.Add((eventType, forms));
            }

            _selectedEventType = _formTreeTabs
                .FirstOrDefault(t => string.Equals(t.EventType, _selectedConfig?.EventTypeName, StringComparison.OrdinalIgnoreCase))
                .EventType
                ?? _formTreeTabs.FirstOrDefault().EventType;
            _selectedFormId = ActiveFormOptions.FirstOrDefault()?.Id;

            BuildQuestionLookups();
            RefreshMappingDisplayNames();
        }
        catch (Exception ex)
        {
            _formTreeError = $"加载表单树异常：{ex.Message}";
        }
        finally
        {
            _cardsLoading = false;
        }
    }

    private void BuildQuestionLookups()
    {
        _questionLookup.Clear();
        _cardNameLookup.Clear();

        foreach (var (_, forms) in _formTreeTabs)
        {
            foreach (var form in forms)
            {
                foreach (var orphan in form.OrphanQuestions)
                {
                    if (!_questionLookup.ContainsKey(orphan.Id))
                    {
                        _questionLookup[orphan.Id] = orphan;
                    }
                }

                foreach (var card in form.Cards)
                {
                    IndexCardNode(card);
                }
            }
        }
    }

    private void IndexCardNode(CardNode card)
    {
        _cardNameLookup[card.CardId] = card.Name;

        foreach (var question in card.Questions)
        {
            if (!_questionLookup.ContainsKey(question.Id))
            {
                _questionLookup[question.Id] = question;
            }
        }

        foreach (var sub in card.SubCards)
        {
            IndexCardNode(sub);
        }
    }

    private void OnEventTypeChanged(string? eventType)
    {
        if (_selectedEventType == eventType)
        {
            return;
        }

        _selectedEventType = eventType;
        _selectedFormId = ActiveFormOptions.FirstOrDefault()?.Id;
        ClearQuestionContextSelection();
    }

    private void OnFormChanged(string? formId)
    {
        if (_selectedFormId == formId)
        {
            return;
        }

        _selectedFormId = formId;
        ClearQuestionContextSelection();
    }

    private void ClearQuestionContextSelection()
    {
        _selectedTreeCardId = null;
        _selectedTreeCardName = null;
        _selectedQuestionNodeType = null;
        _selectedQuestionTreeNodeKey = null;
        _selectedQuestionField = null;
        _activeMappingKey = null;
        ClearPathPickState();
        _llmSuggestions = [];
        _llmError = null;
        _suggestionPanelExpanded = false;
    }

    private void ClearPathPickState()
    {
        _pathPickMode = PathPickMode.None;
        _pathPickTargetKey = null;
        _pathPickTargetKind = PathPickTargetKind.SourcePath;
        _pathPickRuleIndex = -1;
        _pathPickArrayCardId = null;
        _advanceAfterCurrentPick = false;
        SetJsonTreeSelection(null);
    }

    private void SetPathPickState(
        PathPickTargetKind targetKind,
        string? targetKey,
        bool advanceAfterPick,
        string? treeSelectionPath,
        int filterRuleIndex = -1,
        Guid? arrayCardId = null)
    {
        _pathPickMode = PathPickMode.ActiveRow;
        _pathPickTargetKind = targetKind;
        _pathPickTargetKey = targetKey;
        _pathPickRuleIndex = filterRuleIndex;
        _pathPickArrayCardId = arrayCardId;
        _advanceAfterCurrentPick = advanceAfterPick;
        SetJsonTreeSelection(treeSelectionPath);
    }

    private void SetJsonTreeSelection(string? path)
    {
        _selectedJsonTreePath = NormalizeJsonTreeSelectionPath(path);
        _selectedJsonTreeVersion++;
    }

    private static string? NormalizeJsonTreeSelectionPath(string? path)
    {
        var normalized = path?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return SubCardPathHelper.IsAbsoluteJsonPath(normalized)
            ? normalized[2..]
            : normalized;
    }

    private string? BuildJsonTreeSelectionPath(
        MappingTarget mappingTarget,
        Guid? cardId,
        string? arrayPath,
        string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return null;
        }

        var normalizedSource = sourcePath.Trim();
        if (mappingTarget != MappingTarget.SubCard)
        {
            return TryExpandMainRecordScopedPath(normalizedSource, out var scopedPath)
                ? NormalizeJsonTreeSelectionPath(scopedPath)
                : NormalizeJsonTreeSelectionPath(normalizedSource);
        }

        normalizedSource = NormalizeSubCardSourcePathForPreview(normalizedSource);
        var effectiveArrayPath = GetEffectiveArrayPath(cardId, arrayPath, normalizedSource);

        if (!string.IsNullOrWhiteSpace(effectiveArrayPath)
            && SubCardPathHelper.PathsEqual(effectiveArrayPath, "$")
            && !SubCardPathHelper.IsAbsoluteJsonPath(normalizedSource)
            && !IsMainRecordScopedPath(normalizedSource)
            && !SubCardPathHelper.HasArrayWildcard(normalizedSource))
        {
            return NormalizeJsonTreeSelectionPath(normalizedSource);
        }

        if (TryExpandMainRecordScopedPath(normalizedSource, out var subCardMainRecordScopedPath))
        {
            return NormalizeJsonTreeSelectionPath(subCardMainRecordScopedPath);
        }

        if (SubCardPathHelper.IsAbsoluteJsonPath(normalizedSource)
            || (!string.IsNullOrWhiteSpace(effectiveArrayPath)
                && SubCardPathHelper.IsRootScopedPath(normalizedSource, effectiveArrayPath))
            || TryInferSubCardArrayPathFromSourcePath(normalizedSource, out _))
        {
            return NormalizeJsonTreeSelectionPath(normalizedSource);
        }

        if (string.IsNullOrWhiteSpace(effectiveArrayPath))
        {
            return null;
        }

        return NormalizeJsonTreeSelectionPath(BuildSubCardScopedPath(effectiveArrayPath, normalizedSource));
    }

    private bool TryExpandMainRecordScopedPath(string? sourcePath, out string scopedPath)
    {
        scopedPath = "";
        var normalizedSource = sourcePath?.Trim() ?? "";
        var mainRecordArrayPath = GetMainRecordArrayPath();
        var mainRecordContext = GetConfiguredMainRecordContext();
        if (string.IsNullOrWhiteSpace(normalizedSource)
            || string.IsNullOrWhiteSpace(mainRecordArrayPath)
            || mainRecordContext == null
            || SubCardPathHelper.IsAbsoluteJsonPath(normalizedSource)
            || SubCardPathHelper.IsRootScopedPath(normalizedSource, mainRecordArrayPath))
        {
            return false;
        }

        var relativePath = IsMainRecordScopedPath(normalizedSource)
            ? TrimMainRecordScopePrefix(normalizedSource)
            : normalizedSource;
        if (string.IsNullOrWhiteSpace(relativePath)
            || SubCardPathHelper.ResolveFirstToken(mainRecordContext, relativePath) == null)
        {
            return false;
        }

        scopedPath = BuildScopedPath(mainRecordArrayPath, relativePath);
        return true;
    }

    private string? BuildArrayPathTreeSelectionPath(string? arrayPath)
    {
        var effectiveArrayPath = ExpandSubCardArrayPathToRoot(arrayPath);
        return string.IsNullOrWhiteSpace(effectiveArrayPath)
            ? null
            : NormalizeJsonTreeSelectionPath(effectiveArrayPath);
    }

    private string BuildScopedPath(string arrayPath, string sourcePath)
        => SubCardPathHelper.BuildScopedPath(_parsedJson, arrayPath, sourcePath);

    private string BuildSubCardScopedPath(string arrayPath, string sourcePath) =>
        BuildScopedPath(arrayPath, sourcePath);

    private string NormalizePickedArrayPath(string path)
    {
        if (SubCardPathHelper.TrySplitWildcardPath(path, out var pickedArrayPath, out _))
        {
            return NormalizeEditableSubCardArrayPathValue(pickedArrayPath) ?? "";
        }

        return NormalizeEditableSubCardArrayPathValue(path) ?? "";
    }

    private bool TryInferSubCardArrayPathFromSourcePath(string? sourcePath, out string arrayPath)
    {
        arrayPath = "";
        return TryInferSubCardContainerPath(sourcePath, out arrayPath, out _);
    }

    private bool TryInferSubCardContainerPath(
        string? sourcePath,
        out string arrayPath,
        out string relativePath,
        bool allowMainRecordRoot = true)
    {
        arrayPath = "";
        relativePath = "";
        if (_parsedJson == null)
        {
            return false;
        }

        var normalizedSource = SubCardPathHelper.NormalizeArrayPath(sourcePath);
        var mainRecordArrayPath = GetMainRecordArrayPath();
        var mainContext = GetConfiguredMainRecordContext();
        if (!string.IsNullOrWhiteSpace(mainRecordArrayPath)
            && SubCardPathHelper.TryBuildRelativePath(normalizedSource, mainRecordArrayPath, out var mainRelativePath)
            && !string.IsNullOrWhiteSpace(mainRelativePath))
        {
            if (SubCardPathHelper.TrySplitWildcardPath(mainRelativePath, out var nestedArrayPath, out var nestedRelativePath)
                && !string.IsNullOrWhiteSpace(nestedRelativePath))
            {
                arrayPath = NormalizeEditableSubCardArrayPathValue(nestedArrayPath) ?? nestedArrayPath;
                relativePath = nestedRelativePath;
                return true;
            }

            if (SubCardPathHelper.TryInferObjectContainerPath(mainContext, mainRelativePath, out var objectPath, out var objectRelativePath))
            {
                arrayPath = NormalizeEditableSubCardArrayPathValue(objectPath) ?? objectPath;
                relativePath = objectRelativePath;
                return true;
            }

            if (allowMainRecordRoot
                && mainContext != null
                && SubCardPathHelper.ResolveFirstToken(mainContext, mainRelativePath) != null)
            {
                arrayPath = SubCardPathHelper.MainRecordContainerPath;
                relativePath = mainRelativePath;
                return true;
            }
        }

        if (SubCardPathHelper.TrySplitWildcardPath(normalizedSource, out var parsedArrayPath, out var parsedRelativePath)
            && !string.IsNullOrWhiteSpace(parsedRelativePath))
        {
            if (MessageJsonHelper.SelectSampleToken(_parsedJson, parsedArrayPath) is JArray
                || mainContext != null && MessageJsonHelper.SelectSampleToken(mainContext, parsedArrayPath) is JArray)
            {
                arrayPath = NormalizeEditableSubCardArrayPathValue(parsedArrayPath) ?? parsedArrayPath;
                relativePath = parsedRelativePath;
                return true;
            }
        }

        if (SubCardPathHelper.TryInferObjectContainerPath(_parsedJson, normalizedSource, out var rootObjectPath, out var rootObjectRelativePath))
        {
            arrayPath = NormalizeEditableSubCardArrayPathValue(rootObjectPath) ?? rootObjectPath;
            relativePath = rootObjectRelativePath;
            return true;
        }

        if (allowMainRecordRoot
            && !string.IsNullOrWhiteSpace(mainRecordArrayPath)
            && mainContext != null
            && SubCardPathHelper.ResolveFirstToken(mainContext, normalizedSource) != null)
        {
            arrayPath = SubCardPathHelper.MainRecordContainerPath;
            relativePath = normalizedSource;
            return true;
        }

        return false;
    }

    private void HandleWorkbenchBackgroundClick()
    {
        if (_pathPickMode != PathPickMode.None)
        {
            ClearPathPickState();
        }
    }

    private bool IsCardSelectable(CardNode card) =>
        _selectedGenerateTarget switch
        {
            MappingTarget.Question => true,
            MappingTarget.SubCard => IsSubCardNode(card),
            _ => false,
        };

    private void OnTreeCardSelected(Guid cardId, string cardName)
    {
        if (ActiveForm == null)
        {
            return;
        }

        var card = FindCardNode(ActiveForm.Cards, cardId);
        if (card == null)
        {
            return;
        }

        SelectQuestionTreeCardNode(card);
    }

    private static readonly IReadOnlyList<TargetFieldDescriptor> CatalogPatientTargetFields = PatientFieldCatalog.Definitions
        .Select(f => new TargetFieldDescriptor
        {
            FieldId = f.Name,
            DisplayName = f.Label,
            MappingTarget = MappingTarget.Patient,
            DataType = f.DataType
        })
        .ToList();

    private static PatientFieldDefinition? GetPatientFieldDefinition(string fieldId) =>
        PatientFieldCatalog.Definitions.FirstOrDefault(f => string.Equals(f.Name, fieldId, StringComparison.OrdinalIgnoreCase));

    private List<TargetFieldDescriptor> GetCurrentTargetFields(bool applySearchFilters)
    {
        IEnumerable<TargetFieldDescriptor> fields = _selectedGenerateTarget switch
        {
            MappingTarget.Patient => CatalogPatientTargetFields,
            MappingTarget.Event => EventTargetFields,
            MappingTarget.Question => GetCurrentQuestionTargetFields(),
            MappingTarget.SubCard => GetCurrentSubCardGroupFields(),
            _ => Enumerable.Empty<TargetFieldDescriptor>(),
        };

        if (applySearchFilters)
        {
            if (_showOnlyUnmappedTargets)
            {
                fields = fields.Where(f => GetTargetFieldState(f) == TargetFieldState.Unmapped);
            }

            if (!string.IsNullOrWhiteSpace(_targetSearchText))
            {
                var keyword = _targetSearchText.Trim();
                fields = fields.Where(f =>
                    f.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    || f.FieldId.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrWhiteSpace(f.DataType) && f.DataType.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
            }
        }

        return fields.ToList();
    }

    private IEnumerable<TargetFieldDescriptor> GetCurrentQuestionTargetFields()
    {
        if (!RequiresCardSelection || !_selectedTreeCardId.HasValue || ActiveForm == null)
        {
            return Enumerable.Empty<TargetFieldDescriptor>();
        }

        var card = FindCardNode(ActiveForm.Cards, _selectedTreeCardId.Value);
        if (card == null)
        {
            return Enumerable.Empty<TargetFieldDescriptor>();
        }

        var subCardAncestor = FindSubCardAncestor(card, ActiveForm.Cards);
        return EnumerateQuestionFieldsForNode(card, subCardAncestor, includeDescendants: false);
    }

    private static bool IsSubCardNode(CardNode card) => card.CardType is "multiple" or "table";

    private static string GetQuestionTreeNodeKey(CardNode card) =>
        $"{(IsSubCardNode(card) ? "subcard" : "card")}:{card.CardId}";

    private static string GetQuestionTreeNodeKey(TargetFieldDescriptor field) =>
        $"question:{GetMappingKey(field)}";

    private bool IsQuestionTreeNodeSelected(string nodeKey) =>
        string.Equals(_selectedQuestionTreeNodeKey, nodeKey, StringComparison.OrdinalIgnoreCase);

    private IEnumerable<TargetFieldDescriptor> EnumerateQuestionFieldsForNode(
        CardNode card,
        CardNode? subCardAncestor,
        bool includeDescendants)
    {
        var isSubCard = IsSubCardNode(card);
        var currentSubCard = isSubCard ? card : subCardAncestor;

        foreach (var child in FormBrowserHelper.GetOrderedChildren(card))
        {
            if (child.Question != null)
            {
                yield return CreateQuestionFieldDescriptor(child.Question, card, currentSubCard);
                continue;
            }

            if (!includeDescendants || child.Card == null)
            {
                continue;
            }

            foreach (var childField in EnumerateQuestionFieldsForNode(child.Card, currentSubCard, includeDescendants: true))
            {
                yield return childField;
            }
        }
    }

    private static TargetFieldDescriptor CreateQuestionFieldDescriptor(QuestionInfo question, CardNode ownerCard, CardNode? subCardContext)
    {
        var mappingTarget = subCardContext != null ? MappingTarget.SubCard : MappingTarget.Question;

        return new TargetFieldDescriptor
        {
            FieldId = question.Id,
            DisplayName = question.Title ?? question.Id,
            DataType = question.DataType,
            SemanticHint = BuildQuestionSemanticHint(question),
            SelectInfo = question.SelectInfo,
            Options = question.Options,
            MappingTarget = mappingTarget,
            CardId = mappingTarget == MappingTarget.SubCard ? subCardContext?.CardId : null,
            CardName = mappingTarget == MappingTarget.SubCard ? subCardContext?.Name : null,
            ScopeCardId = ownerCard.CardId,
            ScopeCardName = ownerCard.Name,
        };
    }

    private static string? BuildQuestionSemanticHint(QuestionInfo question)
    {
        var parts = new[]
        {
            question.LabelText,
            question.PromptText,
            question.DimensionText,
            question.PrefixText,
            question.SuffixText,
            string.IsNullOrWhiteSpace(question.TableName) ? null : $"表名={question.TableName}",
            string.IsNullOrWhiteSpace(question.ColumnName) ? null : $"列名={question.ColumnName}"
        }
        .Where(static part => !string.IsNullOrWhiteSpace(part))
        .Select(static part => part!.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

        return parts.Count == 0 ? null : string.Join("；", parts);
    }

    private static CardNode? FindCardNode(IEnumerable<CardNode> cards, Guid cardId)
    {
        foreach (var card in cards)
        {
            if (card.CardId == cardId)
            {
                return card;
            }

            var result = FindCardNode(card.SubCards, cardId);
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

    private static CardNode? FindSubCardAncestor(CardNode target, IEnumerable<CardNode> roots)
    {
        foreach (var root in roots)
        {
            var result = FindSubCardAncestorRecursive(target, root, null);
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

    private static CardNode? FindSubCardAncestorRecursive(CardNode target, CardNode current, CardNode? subCardAncestor)
    {
        var currentSubCard = IsSubCardNode(current) ? current : subCardAncestor;
        if (current.CardId == target.CardId)
        {
            return currentSubCard;
        }

        foreach (var sub in current.SubCards)
        {
            var result = FindSubCardAncestorRecursive(target, sub, currentSubCard);
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }

    private void SelectQuestionTreeCardNode(CardNode card)
    {
        _selectedTreeCardId = card.CardId;
        _selectedTreeCardName = card.Name;
        _selectedQuestionNodeType = IsSubCardNode(card) ? QuestionTreeNodeType.SubCard : QuestionTreeNodeType.Card;
        _selectedQuestionTreeNodeKey = GetQuestionTreeNodeKey(card);
        _selectedQuestionField = null;
        _activeMappingKey = null;
        ClearPathPickState();
        _llmSuggestions = [];
        _llmError = null;
        _suggestionPanelExpanded = false;
    }

    private void SelectQuestionTreeQuestion(TargetFieldDescriptor field) =>
        SelectQuestionField(field, armPathPick: true, advanceAfterPick: true);

    private void SelectQuestionField(TargetFieldDescriptor field, bool armPathPick, bool advanceAfterPick)
    {
        _selectedTreeCardId = field.ScopeCardId;
        _selectedTreeCardName = field.ScopeCardName;
        _selectedQuestionNodeType = QuestionTreeNodeType.Question;
        _selectedQuestionTreeNodeKey = GetQuestionTreeNodeKey(field);
        _selectedQuestionField = field;
        _llmSuggestions = [];
        _llmError = null;
        _suggestionPanelExpanded = false;

        var row = FindMappingRow(field);
        if (row != null)
        {
            UpdateRowFromTargetField(row, field);
            _activeMappingKey = GetMappingKey(row);
        }
        else
        {
            _activeMappingKey = null;
        }

        if (armPathPick && ShouldArmPathPick(row))
        {
            ArmPathPick(field, advanceAfterPick);
        }
        else
        {
            ClearPathPickState();
        }
    }

    private void SelectTargetField(TargetFieldDescriptor field)
    {
        _llmSuggestions = [];
        _llmError = null;
        _suggestionPanelExpanded = false;

        var row = FindMappingRow(field);
        if (row != null)
        {
            UpdateRowFromTargetField(row, field);
        }

        _activeMappingKey = GetMappingKey(field);
        if (IsQuestionWorkbenchMode && field.MappingTarget is MappingTarget.Question or MappingTarget.SubCard)
        {
            _selectedQuestionNodeType = QuestionTreeNodeType.Question;
            _selectedQuestionTreeNodeKey = GetQuestionTreeNodeKey(field);
            _selectedQuestionField = field;
            _selectedTreeCardId = field.ScopeCardId ?? _selectedTreeCardId;
            _selectedTreeCardName = field.ScopeCardName ?? _selectedTreeCardName;
        }

        if (ShouldArmPathPick(row))
        {
            ArmPathPick(field, advanceAfterPick: true);
        }
        else
        {
            ClearPathPickState();
        }
    }

    private WizardMappingRow CreateRowFromTargetField(TargetFieldDescriptor field)
    {
        return new WizardMappingRow
        {
            MappingTarget = field.MappingTarget,
            TargetField = field.FieldId,
            TargetFieldDisplayName = field.DisplayName,
            CardId = field.MappingTarget == MappingTarget.SubCard ? field.CardId : null,
            CardName = field.MappingTarget == MappingTarget.SubCard ? field.CardName : null,
            ArrayPath = field.MappingTarget == MappingTarget.SubCard && field.CardId.HasValue
                ? GetExistingArrayPath(field.CardId.Value)
                : null,
            Origin = "manual",
            IsEnabled = true,
        };
    }

    private WizardMappingRow EnsureMappingRow(TargetFieldDescriptor field)
    {
        var row = FindMappingRow(field);
        if (row != null)
        {
            UpdateRowFromTargetField(row, field);
            return row;
        }

        row = CreateRowFromTargetField(field);
        _mappingRows.Add(row);
        return row;
    }

    private void UpdateRowFromTargetField(WizardMappingRow row, TargetFieldDescriptor field)
    {
        row.TargetFieldDisplayName = field.DisplayName;
        row.CardName = field.CardName ?? row.CardName;
        if (row.MappingTarget == MappingTarget.SubCard && row.CardId == null)
        {
            row.CardId = field.CardId;
        }

        if (row.MappingTarget == MappingTarget.SubCard
            && string.IsNullOrWhiteSpace(row.ArrayPath)
            && field.CardId.HasValue)
        {
            row.ArrayPath = GetExistingArrayPath(field.CardId.Value);
        }
    }

    private static bool ShouldArmPathPick(WizardMappingRow? row) =>
        row == null || !HasValueSource(row);

    private void ArmPathPick(TargetFieldDescriptor field, bool advanceAfterPick)
    {
        SetPathPickState(
            targetKind: PathPickTargetKind.SourcePath,
            targetKey: GetMappingKey(field),
            advanceAfterPick: advanceAfterPick,
            treeSelectionPath: null,
            arrayCardId: field.MappingTarget == MappingTarget.SubCard ? field.CardId : null);
    }

    private void ArmActiveRowPathPick(bool advanceAfterPick)
    {
        if (IsQuestionWorkbenchMode && SelectedQuestionField != null)
        {
            ArmPathPick(SelectedQuestionField, advanceAfterPick);
            return;
        }

        if (ActiveRow != null)
        {
            SetPathPickState(
                targetKind: PathPickTargetKind.SourcePath,
                targetKey: GetMappingKey(ActiveRow),
                advanceAfterPick: advanceAfterPick,
                treeSelectionPath: null,
                arrayCardId: ActiveRow.MappingTarget == MappingTarget.SubCard ? ActiveRow.CardId : null);
            return;
        }

        if (SelectedStandaloneField == null)
        {
            return;
        }

        ArmPathPick(SelectedStandaloneField, advanceAfterPick);
    }

    private void ActivateQuestionFieldSourcePath(TargetFieldDescriptor field)
    {
        _activeMappingKey = GetMappingKey(field);
        var row = FindMappingRow(field);
        SetPathPickState(
            targetKind: PathPickTargetKind.SourcePath,
            targetKey: GetMappingKey(field),
            advanceAfterPick: false,
            treeSelectionPath: BuildJsonTreeSelectionPath(
                field.MappingTarget,
                field.CardId,
                row?.ArrayPath ?? (field.CardId.HasValue ? GetSubCardArrayPath(field.CardId.Value) : null),
                row?.SourcePath),
            arrayCardId: field.MappingTarget == MappingTarget.SubCard ? field.CardId : null);
    }

    private void ActivateActiveRowSourcePath()
    {
        if (IsQuestionWorkbenchMode && SelectedQuestionField != null)
        {
            ActivateQuestionFieldSourcePath(SelectedQuestionField);
            return;
        }

        if (ActiveRow != null)
        {
            SetPathPickState(
                targetKind: PathPickTargetKind.SourcePath,
                targetKey: GetMappingKey(ActiveRow),
                advanceAfterPick: false,
                treeSelectionPath: BuildJsonTreeSelectionPath(ActiveRow.MappingTarget, ActiveRow.CardId, ActiveRow.ArrayPath, ActiveRow.SourcePath),
                arrayCardId: ActiveRow.MappingTarget == MappingTarget.SubCard ? ActiveRow.CardId : null);
            return;
        }

        if (SelectedStandaloneField == null)
        {
            return;
        }

        ActivateQuestionFieldSourcePath(SelectedStandaloneField);
    }

    private void ActivateQuestionFieldFilterRulePath(TargetFieldDescriptor field, int ruleIndex)
    {
        _activeMappingKey = GetMappingKey(field);
        var row = FindMappingRow(field);
        var rulePath = row != null && ruleIndex >= 0 && ruleIndex < row.FilterRules.Count
            ? row.FilterRules[ruleIndex].SourcePath
            : null;

        SetPathPickState(
            targetKind: PathPickTargetKind.FilterRule,
            targetKey: GetMappingKey(field),
            advanceAfterPick: false,
            treeSelectionPath: BuildJsonTreeSelectionPath(
                field.MappingTarget,
                field.CardId,
                row?.ArrayPath ?? (field.CardId.HasValue ? GetSubCardArrayPath(field.CardId.Value) : null),
                rulePath),
            filterRuleIndex: ruleIndex,
            arrayCardId: field.MappingTarget == MappingTarget.SubCard ? field.CardId : null);
    }

    private void ActivateQuestionFieldArrayPath(TargetFieldDescriptor field)
    {
        if (field.MappingTarget != MappingTarget.SubCard || !field.CardId.HasValue)
        {
            return;
        }

        _activeMappingKey = GetMappingKey(field);
        SetPathPickState(
            targetKind: PathPickTargetKind.ArrayPath,
            targetKey: GetMappingKey(field),
            advanceAfterPick: false,
            treeSelectionPath: BuildArrayPathTreeSelectionPath(GetQuestionFieldArrayPath(field)),
            arrayCardId: field.CardId.Value);
    }

    private void ActivateSelectedSubCardArrayPath(Guid cardId)
    {
        SetPathPickState(
            PathPickTargetKind.ArrayPath,
            targetKey: null,
            advanceAfterPick: false,
            treeSelectionPath: BuildArrayPathTreeSelectionPath(GetSubCardArrayPath(cardId)),
            arrayCardId: cardId);
    }

    private void ActivateSubCardFilterRulePath(Guid cardId, int ruleIndex)
    {
        var rules = GetSubCardFilterRules(cardId);
        var rulePath = ruleIndex >= 0 && ruleIndex < rules.Count
            ? rules[ruleIndex].SourcePath
            : null;

        SetPathPickState(
            targetKind: PathPickTargetKind.SubCardFilterRule,
            targetKey: GetSubCardFilterKey(cardId),
            advanceAfterPick: false,
            treeSelectionPath: BuildJsonTreeSelectionPath(
                MappingTarget.SubCard,
                cardId,
                GetSubCardArrayPath(cardId),
                rulePath),
            filterRuleIndex: ruleIndex,
            arrayCardId: cardId);
    }

    private WizardMappingRow? ResolveCurrentPathPickRow(bool createIfMissing)
    {
        if (string.IsNullOrWhiteSpace(_pathPickTargetKey))
        {
            return null;
        }

        var row = _mappingRows.FirstOrDefault(r =>
            string.Equals(GetMappingKey(r), _pathPickTargetKey, StringComparison.OrdinalIgnoreCase));

        if (row != null || !createIfMissing)
        {
            return row;
        }

        var field = FindCurrentTargetField(_pathPickTargetKey);
        if (field == null)
        {
            return null;
        }

        row = EnsureMappingRow(field);
        _activeMappingKey = GetMappingKey(row);
        return row;
    }

    private async Task<string?> NormalizePickedPathForRowAsync(WizardMappingRow row, string path, string? currentPath)
    {
        if (row.MappingTarget != MappingTarget.SubCard)
        {
            return TryNormalizeMainRecordRelativeSourcePath(path, out var normalizedMainRecordPath)
                ? normalizedMainRecordPath
                : path;
        }

        var normalizedPath = SubCardPathHelper.NormalizeArrayPath(path);
        var effectiveArrayPath = GetEffectiveArrayPath(row.CardId, row.ArrayPath);
        if (!string.IsNullOrWhiteSpace(effectiveArrayPath))
        {
            if (SubCardPathHelper.PathsEqual(effectiveArrayPath, "$")
                && !SubCardPathHelper.IsAbsoluteJsonPath(normalizedPath)
                && !IsMainRecordScopedPath(normalizedPath))
            {
                row.ArrayPath = effectiveArrayPath;
                SyncSubCardArrayPath(row);
                return normalizedPath;
            }

            if (SubCardPathHelper.TryBuildRelativePath(normalizedPath, effectiveArrayPath, out var objectRelativePath))
            {
                if (string.IsNullOrWhiteSpace(objectRelativePath))
                {
                    inj_snackbar.Add("子卡源路径必须选到容器内的具体字段。", Severity.Warning);
                    return currentPath;
                }

                row.ArrayPath = effectiveArrayPath;
                SyncSubCardArrayPath(row);
                return objectRelativePath;
            }
        }

        if (TryInferSubCardContainerPath(
                normalizedPath,
                out var inferredArrayPath,
                out var inferredRelativePath,
                string.IsNullOrWhiteSpace(effectiveArrayPath))
            && !string.IsNullOrWhiteSpace(inferredRelativePath))
        {
            row.ArrayPath = inferredArrayPath;
            SyncSubCardArrayPath(row);
            return inferredRelativePath;
        }

        if (TryNormalizeMainRecordSourcePath(normalizedPath, out var subCardMainRecordScopedPath)
            && !SubCardPathHelper.HasArrayWildcard(TrimMainRecordScopePrefix(subCardMainRecordScopedPath)))
        {
            return subCardMainRecordScopedPath;
        }

        if (!SubCardPathHelper.TrySplitWildcardPath(normalizedPath, out var pickedArrayPath, out var relativePath))
        {
            return SubCardPathHelper.EnsureAbsoluteJsonPath(normalizedPath);
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            inj_snackbar.Add("子卡源路径必须选到数组项内的具体字段。", Severity.Warning);
            return currentPath;
        }

        if (!await TryUpdateSubCardArrayPathFromPickedPathAsync(row, pickedArrayPath))
        {
            return currentPath;
        }

        return relativePath;
    }

    private async Task<string?> NormalizePickedSubCardPathAsync(Guid cardId, string path, string? currentPath)
    {
        var row = new WizardMappingRow
        {
            MappingTarget = MappingTarget.SubCard,
            CardId = cardId,
            ArrayPath = GetSubCardArrayPath(cardId),
            IsEnabled = true,
        };

        var normalizedPath = await NormalizePickedPathForRowAsync(row, path, currentPath);
        if (!string.IsNullOrWhiteSpace(row.ArrayPath))
        {
            SetSubCardArrayPath(cardId, row.ArrayPath);
        }

        return normalizedPath;
    }

    private async Task OnJsonPathSelected(string path)
    {
        if (_pathPickMode != PathPickMode.ActiveRow)
        {
            return;
        }

        if (_pathPickTargetKind == PathPickTargetKind.ArrayPath)
        {
            if (!_pathPickArrayCardId.HasValue)
            {
                ClearPathPickState();
                return;
            }

            var normalizedArrayPath = NormalizePickedArrayPath(path);
            SetSubCardArrayPath(_pathPickArrayCardId.Value, normalizedArrayPath);
            SetJsonTreeSelection(BuildArrayPathTreeSelectionPath(GetSubCardArrayPath(_pathPickArrayCardId.Value)));
            return;
        }

        if (_pathPickTargetKind == PathPickTargetKind.SubCardFilterRule)
        {
            if (!_pathPickArrayCardId.HasValue)
            {
                ClearPathPickState();
                return;
            }

            var rules = GetSubCardFilterRules(_pathPickArrayCardId.Value);
            if (_pathPickRuleIndex < 0 || _pathPickRuleIndex >= rules.Count)
            {
                ClearPathPickState();
                return;
            }

            var normalizedRulePath = await NormalizePickedSubCardPathAsync(
                _pathPickArrayCardId.Value,
                path,
                rules[_pathPickRuleIndex].SourcePath);
            if (normalizedRulePath == null)
            {
                return;
            }

            rules[_pathPickRuleIndex].SourcePath = normalizedRulePath;
            SetJsonTreeSelection(BuildJsonTreeSelectionPath(
                MappingTarget.SubCard,
                _pathPickArrayCardId.Value,
                GetSubCardArrayPath(_pathPickArrayCardId.Value),
                normalizedRulePath));
            return;
        }

        var row = ResolveCurrentPathPickRow(createIfMissing: true);
        if (row == null)
        {
            ClearPathPickState();
            return;
        }

        if (_pathPickTargetKind == PathPickTargetKind.FilterRule)
        {
            if (_pathPickRuleIndex < 0 || _pathPickRuleIndex >= row.FilterRules.Count)
            {
                ClearPathPickState();
                return;
            }

            var normalizedRulePath = await NormalizePickedPathForRowAsync(
                row,
                path,
                row.FilterRules[_pathPickRuleIndex].SourcePath);
            if (normalizedRulePath == null)
            {
                return;
            }

            row.FilterRules[_pathPickRuleIndex].SourcePath = normalizedRulePath;
            _activeMappingKey = GetMappingKey(row);
            SetJsonTreeSelection(BuildJsonTreeSelectionPath(row.MappingTarget, row.CardId, row.ArrayPath, normalizedRulePath));
            return;
        }

        var normalizedSourcePath = await NormalizePickedPathForRowAsync(row, path, row.SourcePath);
        if (normalizedSourcePath == null)
        {
            return;
        }

        row.SourcePath = normalizedSourcePath;
        ValidateMappingRowPath(row);
        _activeMappingKey = GetMappingKey(row);
        SetJsonTreeSelection(BuildJsonTreeSelectionPath(row.MappingTarget, row.CardId, row.ArrayPath, row.SourcePath));
        var shouldAdvance = _advanceAfterCurrentPick && _autoAdvanceAfterPathPick;

        if (shouldAdvance)
        {
            ClearPathPickState();
            TrySelectNextUnmappedField(row);
        }
    }

    private void TrySelectNextUnmappedField(WizardMappingRow currentRow)
    {
        var currentFields = GetCurrentTargetFields(applySearchFilters: true);
        var currentKey = GetMappingKey(currentRow);
        var currentIndex = currentFields.FindIndex(f => GetMappingKey(f) == currentKey);
        if (currentIndex < 0)
        {
            return;
        }

        var nextField = currentFields
            .Skip(currentIndex + 1)
            .FirstOrDefault(f => GetTargetFieldState(f) == TargetFieldState.Unmapped);

        if (nextField != null)
        {
            if (IsQuestionWorkbenchMode && nextField.MappingTarget is MappingTarget.Question or MappingTarget.SubCard)
            {
                SelectQuestionTreeQuestion(nextField);
            }
            else
            {
                SelectTargetField(nextField);
            }
        }
    }

    private async Task<bool> TryApplyPickedSubCardPathAsync(WizardMappingRow row, string path)
    {
        var normalizedPath = SubCardPathHelper.NormalizeArrayPath(path);
        if (TryInferSubCardContainerPath(
                normalizedPath,
                out var inferredArrayPath,
                out var inferredRelativePath,
                string.IsNullOrWhiteSpace(row.ArrayPath))
            && !string.IsNullOrWhiteSpace(inferredRelativePath))
        {
            row.ArrayPath = inferredArrayPath;
            SyncSubCardArrayPath(row);
            row.SourcePath = inferredRelativePath;
            return true;
        }

        if (!SubCardPathHelper.TrySplitWildcardPath(normalizedPath, out var pickedArrayPath, out var relativePath))
        {
            row.SourcePath = SubCardPathHelper.EnsureAbsoluteJsonPath(normalizedPath);
            return true;
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            inj_snackbar.Add("子卡源路径必须选到数组项内的具体字段。", Severity.Warning);
            return false;
        }

        if (!await TryUpdateSubCardArrayPathFromPickedPathAsync(row, pickedArrayPath))
        {
            return false;
        }

        row.SourcePath = relativePath;
        return true;
    }

    private async Task<bool> TryUpdateSubCardArrayPathFromPickedPathAsync(WizardMappingRow row, string pickedArrayPath)
    {
        var currentArrayPath = GetEffectiveArrayPath(row.CardId, row.ArrayPath, row.SourcePath);
        if (!string.IsNullOrWhiteSpace(currentArrayPath)
            && !SubCardPathHelper.PathsEqual(currentArrayPath, pickedArrayPath))
        {
            var confirmed = await inj_dialogService.ShowMessageBox(
                "数组路径确认",
                $"当前子卡字段【{row.TargetFieldDisplayName}】的 ArrayPath 为：{currentArrayPath}\n新选择的路径属于：{pickedArrayPath}\n是否更新为新数组路径？",
                yesText: "更新",
                cancelText: "取消");
            if (confirmed != true)
            {
                return false;
            }
        }

        row.ArrayPath = pickedArrayPath;
        SyncSubCardArrayPath(row);
        return true;
    }

    private IEnumerable<TargetFieldDescriptor> EnumerateResolvableTargetFields()
    {
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var field in GetCurrentTargetFields(applySearchFilters: false))
        {
            if (seenKeys.Add(GetMappingKey(field)))
            {
                yield return field;
            }
        }

        if (!IsQuestionWorkbenchMode)
        {
            yield break;
        }

        foreach (var field in GetCurrentQuestionScopeTargetFields())
        {
            if (seenKeys.Add(GetMappingKey(field)))
            {
                yield return field;
            }
        }

        foreach (var field in GetCurrentSubCardGroupFields())
        {
            if (seenKeys.Add(GetMappingKey(field)))
            {
                yield return field;
            }
        }
    }

    private TargetFieldDescriptor? FindCurrentTargetField(string? mappingKey)
    {
        if (string.IsNullOrWhiteSpace(mappingKey))
        {
            return null;
        }

        if (SelectedQuestionField != null
            && string.Equals(GetMappingKey(SelectedQuestionField), mappingKey, StringComparison.OrdinalIgnoreCase))
        {
            return SelectedQuestionField;
        }

        return EnumerateResolvableTargetFields()
            .FirstOrDefault(field => string.Equals(GetMappingKey(field), mappingKey, StringComparison.OrdinalIgnoreCase));
    }

    private void PreloadSubCardFilterRules(
        IEnumerable<EsbFieldMapping> mappings,
        IReadOnlyDictionary<int, List<EsbFilterRule>> mappingRuleMap)
    {
        foreach (var mapping in mappings.Where(EsbFieldMapping.IsSubCardFilterMapping))
        {
            if (!mapping.CardId.HasValue)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(mapping.ArrayPath))
            {
                _subCardArrayPathOverrides[mapping.CardId.Value] = NormalizeEditableSubCardArrayPathValue(mapping.ArrayPath);
            }

            _subCardFilterRulesByCardId[mapping.CardId.Value] =
                mappingRuleMap.TryGetValue(mapping.Id, out var rules) ? CloneFilterRules(rules) : [];
        }
    }

    private void PreloadExistingMappings(Dictionary<int, List<EsbFilterRule>> mappingRuleMap)
    {
        _mappingRows = _existingMappings.Select(m => new WizardMappingRow
        {
            MappingTarget = m.MappingTarget,
            SourcePath = m.SourcePath,
            TargetField = m.TargetField,
            TargetFieldDisplayName = ResolveTargetFieldDisplayName(m.MappingTarget, m.TargetField),
            DictCode = m.DictCode,
            DictMatchMode = EsbFieldMapping.NormalizeDictMatchMode(m.DictMatchMode),
            DefaultValue = m.DefaultValue,
            ValueExpression = m.ValueExpression,
            CardId = m.CardId,
            CardName = m.CardId.HasValue && _cardNameLookup.TryGetValue(m.CardId.Value, out var cardName) ? cardName : null,
            ArrayPath = NormalizeEditableSubCardArrayPathValue(m.ArrayPath),
            IsRequired = m.IsRequired,
            IsEnabled = m.IsEnabled,
            Origin = "existing",
            ExistingId = m.Id,
            FilterRules = mappingRuleMap.TryGetValue(m.Id, out var rules) ? CloneFilterRules(rules) : [],
        }).ToList();

        foreach (var row in _mappingRows)
        {
            row.SourcePath = NormalizeEditableSourcePath(row.MappingTarget, row.CardId, row.ArrayPath, row.SourcePath);
            ValidateMappingRowPath(row);
        }
    }

    private void RefreshMappingDisplayNames()
    {
        foreach (var row in _mappingRows)
        {
            row.TargetFieldDisplayName = ResolveTargetFieldDisplayName(row.MappingTarget, row.TargetField);
            if (row.CardId.HasValue && string.IsNullOrWhiteSpace(row.CardName) && _cardNameLookup.TryGetValue(row.CardId.Value, out var cardName))
            {
                row.CardName = cardName;
            }
        }
    }

    private string ResolveTargetFieldDisplayName(MappingTarget target, string targetField)
    {
        return target switch
        {
            MappingTarget.Patient => GetPatientFieldDefinition(targetField)?.Label ?? targetField,
            MappingTarget.Event => EventTargetFields.FirstOrDefault(f => f.FieldId == targetField)?.DisplayName ?? targetField,
            MappingTarget.Question or MappingTarget.SubCard => _questionLookup.TryGetValue(targetField, out var q)
                ? q.Title ?? targetField
                : targetField,
            _ => targetField,
        };
    }

    private static string GetMappingKey(MappingTarget target, string targetField, Guid? cardId = null) =>
        target == MappingTarget.SubCard
            ? $"{target}:{cardId}:{targetField}"
            : $"{target}:{targetField}";

    private static string GetMappingKey(WizardMappingRow row) => GetMappingKey(row.MappingTarget, row.TargetField, row.CardId);

    private static string GetMappingKey(TargetFieldDescriptor field) => GetMappingKey(field.MappingTarget, field.FieldId, field.CardId);

    private WizardMappingRow? FindMappingRow(TargetFieldDescriptor field)
    {
        var key = GetMappingKey(field);
        return _mappingRows.FirstOrDefault(r => string.Equals(GetMappingKey(r), key, StringComparison.OrdinalIgnoreCase));
    }

    private TargetFieldState GetTargetFieldState(TargetFieldDescriptor field)
    {
        var row = FindMappingRow(field);
        if (row == null)
        {
            return TargetFieldState.Unmapped;
        }

        if (!row.IsEnabled)
        {
            return TargetFieldState.Disabled;
        }

        if (!HasValueSource(row))
        {
            return TargetFieldState.Pending;
        }

        if (HasIncompleteSubCardContext(row))
        {
            return TargetFieldState.Pending;
        }

        if (HasInvalidSubCardArrayPath(row))
        {
            return TargetFieldState.Invalid;
        }

        if (!string.IsNullOrWhiteSpace(row.SourcePath) && !row.IsPathValid)
        {
            return TargetFieldState.Invalid;
        }

        return TargetFieldState.Mapped;
    }

    private static Color GetStateColor(TargetFieldState state) => state switch
    {
        TargetFieldState.Mapped => Color.Success,
        TargetFieldState.Pending => Color.Warning,
        TargetFieldState.Invalid => Color.Error,
        TargetFieldState.Disabled => Color.Dark,
        _ => Color.Default,
    };

    private static string GetStateIcon(TargetFieldState state) => state switch
    {
        TargetFieldState.Mapped => Icons.Material.Filled.CheckCircle,
        TargetFieldState.Pending => Icons.Material.Filled.RadioButtonUnchecked,
        TargetFieldState.Invalid => Icons.Material.Filled.Error,
        TargetFieldState.Disabled => Icons.Material.Filled.PauseCircle,
        _ => Icons.Material.Filled.Circle,
    };

    private static string GetStateText(TargetFieldState state) => state switch
    {
        TargetFieldState.Mapped => "已映射",
        TargetFieldState.Pending => "待补路径或默认值",
        TargetFieldState.Invalid => "路径无效",
        TargetFieldState.Disabled => "已禁用",
        _ => "未映射",
    };

    private string GetTargetFieldSubtitle(TargetFieldDescriptor field, WizardMappingRow? row)
    {
        if (row == null)
        {
            return string.Empty;
        }

        if (!row.IsEnabled)
        {
            return "当前映射已禁用";
        }

        if (!HasValueSource(row))
        {
            return HasSupplementalConfiguration(row) ? "待补路径或默认值" : string.Empty;
        }

        if (string.IsNullOrWhiteSpace(row.SourcePath))
        {
            var defaultValue = row.DefaultValue ?? string.Empty;
            return defaultValue.Length > 40
                ? $"默认值：{defaultValue[..40]}..."
                : $"默认值：{defaultValue}";
        }

        return GetDisplaySourcePath(row);
    }

    private string GetCompactQuestionFieldSubtitle(TargetFieldDescriptor field)
    {
        var row = FindMappingRow(field);
        return GetTargetFieldState(field) switch
        {
            TargetFieldState.Mapped => string.IsNullOrWhiteSpace(row?.SourcePath) ? "已配置默认值" : "已映射",
            TargetFieldState.Pending => "待补路径或默认值",
            TargetFieldState.Invalid => "源路径无效",
            TargetFieldState.Disabled => "已禁用",
            _ => "",
        };
    }

    private string GetCardNodeMetaText(CardNode card)
    {
        var parts = new List<string>
        {
            $"直属 Question {card.Questions.Count}",
        };

        if (card.SubCards.Count > 0)
        {
            parts.Add($"子节点 {card.SubCards.Count}");
        }

        if (IsSubCardNode(card))
        {
            parts.Add(GetSubCardArrayPathStatusText(card.CardId));
        }

        return string.Join(" / ", parts);
    }

    private static Color GetStatusAccentColor(NodeStatusSummary summary)
    {
        if (summary.Invalid > 0)
        {
            return Color.Error;
        }

        if (summary.Pending + summary.Unmapped > 0)
        {
            return Color.Warning;
        }

        return summary.Total > 0 ? Color.Success : Color.Default;
    }

    private NodeStatusSummary BuildNodeStatusSummary(IEnumerable<TargetFieldDescriptor> fields)
    {
        var total = 0;
        var mapped = 0;
        var pending = 0;
        var invalid = 0;
        var disabled = 0;

        foreach (var field in fields)
        {
            total++;
            switch (GetTargetFieldState(field))
            {
                case TargetFieldState.Mapped:
                    mapped++;
                    break;
                case TargetFieldState.Pending:
                    pending++;
                    break;
                case TargetFieldState.Invalid:
                    invalid++;
                    break;
                case TargetFieldState.Disabled:
                    disabled++;
                    break;
            }
        }

        return new NodeStatusSummary(total, mapped, pending, invalid, disabled);
    }

    private NodeStatusSummary GetFormStatusSummary(FormNode form) =>
        BuildNodeStatusSummary(form.Cards.SelectMany(card => EnumerateQuestionFieldsForNode(card, null, includeDescendants: true)));

    private NodeStatusSummary GetCardNodeStatusSummary(CardNode card, CardNode? subCardAncestor) =>
        BuildNodeStatusSummary(GetCardStatusFields(card, subCardAncestor));

    private IEnumerable<TargetFieldDescriptor> GetCardStatusFields(CardNode card, CardNode? subCardAncestor)
    {
        return EnumerateQuestionFieldsForNode(card, subCardAncestor, includeDescendants: false);
    }

    private bool IsQuestionTreeQuestionVisible(QuestionInfo question, CardNode ownerCard, CardNode? subCardContext)
    {
        var descriptor = CreateQuestionFieldDescriptor(question, ownerCard, subCardContext);

        if (_showOnlyUnmappedTargets && GetTargetFieldState(descriptor) != TargetFieldState.Unmapped)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(_targetSearchText))
        {
            return true;
        }

        var keyword = _targetSearchText.Trim();
        return descriptor.DisplayName.Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || descriptor.FieldId.Contains(keyword, StringComparison.OrdinalIgnoreCase)
               || (!string.IsNullOrWhiteSpace(descriptor.DataType)
                   && descriptor.DataType.Contains(keyword, StringComparison.OrdinalIgnoreCase))
               || (descriptor.CardName?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private bool IsQuestionTreeCardVisible(CardNode card, CardNode? subCardAncestor)
    {
        var isSubCard = IsSubCardNode(card);
        var currentSubCard = isSubCard ? card : subCardAncestor;
        var cardMatches = !string.IsNullOrWhiteSpace(_targetSearchText)
                          && (card.Name.Contains(_targetSearchText.Trim(), StringComparison.OrdinalIgnoreCase)
                              || card.CardType.Contains(_targetSearchText.Trim(), StringComparison.OrdinalIgnoreCase));

        if (card.Questions.Any(q => IsQuestionTreeQuestionVisible(q, card, currentSubCard)))
        {
            return true;
        }

        if (card.SubCards.Any(sub => IsQuestionTreeCardVisible(sub, currentSubCard)))
        {
            return true;
        }

        return cardMatches && !_showOnlyUnmappedTargets;
    }

    private string GetSelectedQuestionContextText()
    {
        if (_selectedGenerateTarget is not (MappingTarget.Question or MappingTarget.SubCard))
        {
            return GetTargetDisplayText(_selectedGenerateTarget ?? MappingTarget.Patient);
        }

        if (ActiveForm == null)
        {
            return "等待选择表单";
        }

        if (_selectedQuestionNodeType == null)
        {
            return $"{ActiveForm.Name} / 等待选择树节点";
        }

        if (_selectedQuestionNodeType == QuestionTreeNodeType.Question && SelectedQuestionField != null)
        {
            if (!string.IsNullOrWhiteSpace(SelectedQuestionField.CardName))
            {
                return $"{ActiveForm.Name} / {SelectedQuestionField.CardName} / {GetActivePickTargetName()}";
            }

            return $"{ActiveForm.Name} / {_selectedTreeCardName} / {GetActivePickTargetName()}";
        }

        return $"{ActiveForm.Name} / {_selectedTreeCardName}";
    }

    private string GetSelectedQuestionProgressText()
    {
        if (_selectedGenerateTarget is not (MappingTarget.Question or MappingTarget.SubCard))
        {
            return GetCurrentTargetProgressText();
        }

        if (ActiveForm == null)
        {
            return "等待选择表单";
        }

        if (SelectedScopeCard == null || _selectedQuestionNodeType == null)
        {
            var formSummary = GetFormStatusSummary(ActiveForm);
            return $"当前表单 {formSummary.Mapped}/{formSummary.Total} 已映射，待补 {formSummary.Pending + formSummary.Unmapped}，无效 {formSummary.Invalid}";
        }

        var subCardAncestor = FindSubCardAncestor(SelectedScopeCard, ActiveForm.Cards);
        var summary = GetCardNodeStatusSummary(SelectedScopeCard, subCardAncestor);
        return $"当前节点 {summary.Mapped}/{summary.Total} 已映射，待补 {summary.Pending + summary.Unmapped}，无效 {summary.Invalid}";
    }

    private string GetCurrentScopeText()
    {
        if (_selectedConfig == null)
        {
            return "未选择接口";
        }

        if (_selectedGenerateTarget == null)
        {
            return "未选择目标类型";
        }

        if (_selectedGenerateTarget is MappingTarget.Question or MappingTarget.SubCard)
        {
            return $"{GetTargetDisplayText(_selectedGenerateTarget.Value)} / {_selectedEventType} > {GetSelectedQuestionContextText()}";
        }

        return GetTargetDisplayText(_selectedGenerateTarget.Value);
    }

    private string GetCurrentTargetProgressText()
    {
        if (_selectedGenerateTarget is MappingTarget.Question or MappingTarget.SubCard)
        {
            return GetSelectedQuestionProgressText();
        }

        var fields = GetCurrentTargetFields(applySearchFilters: false);
        if (_selectedGenerateTarget == null)
        {
            return "请选择目标类型";
        }

        if (RequiresCardSelection && !_selectedTreeCardId.HasValue)
        {
            return "等待选择卡片";
        }

        if (fields.Count == 0)
        {
            return "当前范围无字段";
        }

        var mapped = fields.Count(f => GetTargetFieldState(f) == TargetFieldState.Mapped);
        var pending = fields.Count(f => GetTargetFieldState(f) == TargetFieldState.Pending);
        var invalid = fields.Count(f => GetTargetFieldState(f) == TargetFieldState.Invalid);
        return $"当前范围 {mapped}/{fields.Count} 已映射，待补 {pending}，无效 {invalid}";
    }

    private string GetCurrentQuestionContextText()
    {
        if (_selectedGenerateTarget == null)
        {
            return "未选择";
        }

        if (_selectedGenerateTarget is not (MappingTarget.Question or MappingTarget.SubCard))
        {
            return GetTargetDisplayText(_selectedGenerateTarget.Value);
        }

        return GetSelectedQuestionContextText();
    }

    private static string GetTargetDisplayText(MappingTarget target) => target switch
    {
        MappingTarget.Patient => "Patient",
        MappingTarget.Event => "Event",
        MappingTarget.Question => "Question",
        MappingTarget.SubCard => "SubCard",
        _ => target.ToString(),
    };

    private string GetCurrentCardScopeName()
    {
        if (!_selectedTreeCardId.HasValue || ActiveForm == null)
        {
            return _selectedTreeCardName ?? "";
        }

        var card = FindCardNode(ActiveForm.Cards, _selectedTreeCardId.Value);
        if (card == null)
        {
            return _selectedTreeCardName ?? "";
        }

        return card.CardType is "multiple" or "table"
            ? $"{card.Name} / 子卡"
            : card.Name;
    }

    private string GetActivePickTargetName()
    {
        if (_pathPickTargetKind == PathPickTargetKind.SubCardFilterRule && _pathPickArrayCardId.HasValue)
        {
            var cardName = GetPathPickArrayCardName();
            return string.IsNullOrWhiteSpace(cardName) ? "SubCard级过滤" : $"{cardName} / SubCard级过滤";
        }

        if (_pathPickMode == PathPickMode.ActiveRow && !string.IsNullOrWhiteSpace(_pathPickTargetKey))
        {
            var pickedRow = _mappingRows.FirstOrDefault(r =>
                string.Equals(GetMappingKey(r), _pathPickTargetKey, StringComparison.OrdinalIgnoreCase));
            if (pickedRow != null)
            {
                return FormatTargetFieldName(pickedRow.TargetFieldDisplayName, pickedRow.TargetField, pickedRow.MappingTarget);
            }

            var pickedField = FindCurrentTargetField(_pathPickTargetKey);
            if (pickedField != null)
            {
                return FormatTargetFieldName(pickedField.DisplayName, pickedField.FieldId, pickedField.MappingTarget);
            }
        }

        var row = ActiveRow;
        if (row != null)
        {
            return FormatTargetFieldName(row.TargetFieldDisplayName, row.TargetField, row.MappingTarget);
        }

        var field = SelectedQuestionField ?? SelectedStandaloneField;
        if (field == null)
        {
            return "";
        }

        return FormatTargetFieldName(field.DisplayName, field.FieldId, field.MappingTarget);
    }

    private static string FormatTargetFieldName(string? displayName, string fieldId, MappingTarget mappingTarget)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return fieldId;
        }

        return mappingTarget is MappingTarget.Question or MappingTarget.SubCard
            ? displayName
            : $"{displayName}（{fieldId}）";
    }

    private string GetCurrentPathPickPromptText()
    {
        var targetName = GetActivePickTargetName();
        return _pathPickTargetKind switch
        {
            PathPickTargetKind.SubCardFilterRule => string.IsNullOrWhiteSpace(targetName)
                ? $"SubCard级过滤条件 #{_pathPickRuleIndex + 1} 路径"
                : $"{targetName} 条件 #{_pathPickRuleIndex + 1} 路径",
            PathPickTargetKind.FilterRule => string.IsNullOrWhiteSpace(targetName)
                ? $"过滤条件 #{_pathPickRuleIndex + 1} 路径"
                : $"{targetName} 的过滤条件 #{_pathPickRuleIndex + 1} 路径",
            PathPickTargetKind.ArrayPath => string.IsNullOrWhiteSpace(GetPathPickArrayCardName())
                ? "ArrayPath"
                : $"{GetPathPickArrayCardName()} 的 ArrayPath",
            _ => string.IsNullOrWhiteSpace(targetName)
                ? "源路径"
                : $"{targetName} 的源路径",
        };
    }

    private string? GetPathPickArrayCardName()
    {
        if (!_pathPickArrayCardId.HasValue)
        {
            return null;
        }

        if (_cardNameLookup.TryGetValue(_pathPickArrayCardId.Value, out var cardName))
        {
            return cardName;
        }

        if (CurrentSubCardGroupCard?.CardId == _pathPickArrayCardId.Value)
        {
            return CurrentSubCardGroupCard.Name;
        }

        return null;
    }

    private async Task RunLlmSuggest()
    {
        if (_parsedJson == null || _selectedConfig == null || _selectedGenerateTarget == null)
        {
            return;
        }

        if (RequiresCardSelection && !HasQuestionScopeSelection)
        {
            inj_snackbar.Add("请先在树中选择要生成建议的 Card 或 SubCard 节点。", Severity.Warning);
            return;
        }

        _llmSuggestionMode = LlmSuggestionMode.Scope;
        _llmFocusedMappingKey = null;
        _llmFocusedFieldName = null;
        _llmLoading = true;
        _llmError = null;
        _llmSuggestions = [];
        _suggestionPanelExpanded = false;

        try
        {
            var targetFields = await BuildTargetFieldsAsync();
            if (_showOnlyUnmappedTargets && !_isRebuildMode && _existingMappings.Count > 0)
            {
                var mappedKeys = _existingMappings
                    .Select(m => GetMappingKey(m.MappingTarget, m.TargetField, m.CardId))
                    .ToHashSet();
                targetFields = targetFields
                    .Where(f => !mappedKeys.Contains(GetMappingKey(f.Category, f.FieldId, f.CardId)))
                    .ToList();
            }

            if (_showOnlyUnmappedTargets)
            {
                var currentKeys = _mappingRows.Select(GetMappingKey).ToHashSet();
                targetFields = targetFields
                    .Where(f => !currentKeys.Contains(GetMappingKey(f.Category, f.FieldId, f.CardId)))
                    .ToList();
            }

            if (targetFields.Count == 0)
            {
                inj_snackbar.Add("当前范围内所有目标字段都已有映射，无需再生成建议。", Severity.Info);
                return;
            }

            var (suggestions, rawResponse) = await LlmService.SuggestMappingsWithRawAsync(_parsedJson, targetFields, GetLlmScopeHintText(), CancellationToken, GetMainRecordArrayPath());
            var fieldNameMap = targetFields.ToDictionary(f => f.FieldId, f => f.DisplayName);
            var cardNameMap = targetFields
                .Where(f => f.CardName != null)
                .GroupBy(f => f.CardId)
                .ToDictionary(g => g.Key ?? Guid.Empty, g => g.First().CardName ?? "");

            _llmSuggestions = suggestions
                .GroupBy(s => GetMappingKey(s.MappingTarget, s.TargetField, s.CardId))
                .Select(g =>
                {
                    var best = g.OrderByDescending(s => s.Confidence).First();
                    fieldNameMap.TryGetValue(best.TargetField, out var displayName);
                    cardNameMap.TryGetValue(best.CardId ?? Guid.Empty, out var cardName);
                    var normalizedArrayPath = NormalizeEditableSubCardArrayPathValue(best.ArrayPath);
                    return new LlmSuggestionItem
                    {
                        SourcePath = NormalizeSuggestionSourcePath(best.MappingTarget, best.CardId, normalizedArrayPath, best.SourcePath),
                        TargetField = best.TargetField,
                        DisplayName = displayName ?? best.TargetField,
                        MappingTarget = best.MappingTarget,
                        Confidence = best.Confidence,
                        Reason = best.Reason,
                        DictCode = best.DictCode,
                        DefaultValue = best.DefaultValue,
                        CardId = best.CardId,
                        CardName = cardName,
                        ArrayPath = normalizedArrayPath,
                        IsSelected = best.Confidence >= 0.5,
                    };
                })
                .OrderByDescending(s => s.Confidence)
                .ToList();

            if (_llmSuggestions.Count == 0)
            {
                _llmDialogVisible = false;
                var preview = rawResponse.Length > 200 ? rawResponse[..200] + "..." : rawResponse;
                inj_snackbar.Add($"LLM 未返回可用建议。原始返回: {preview}", Severity.Warning);
            }
            else
            {
                _llmDialogVisible = true;
                _suggestionPanelExpanded = false;
                inj_snackbar.Add($"LLM 生成 {_llmSuggestions.Count} 条建议。", Severity.Success);
            }
        }
        catch (Exception ex)
        {
            _llmError = $"LLM 调用失败: {ex.Message}";
        }
        finally
        {
            _llmLoading = false;
        }
    }

    private async Task RunActiveFieldLlmSuggest()
    {
        var activeRow = ActiveRow;
        var selectedField = SelectedQuestionField ?? SelectedStandaloneField;
        if (_parsedJson == null || _selectedConfig == null)
        {
            return;
        }

        TargetFieldInfo? targetField;
        string targetKey;
        if (activeRow != null)
        {
            if (!TryBuildTargetFieldInfo(activeRow, out targetField) || targetField == null)
            {
                inj_snackbar.Add("当前字段缺少目标信息，无法生成 LLM 建议。", Severity.Warning);
                return;
            }

            targetKey = GetMappingKey(activeRow);
            _llmFocusedFieldName = string.IsNullOrWhiteSpace(activeRow.TargetFieldDisplayName)
                ? activeRow.TargetField
                : $"{activeRow.TargetFieldDisplayName}（{activeRow.TargetField}）";
        }
        else if (selectedField != null)
        {
            if (!TryBuildTargetFieldInfo(selectedField, out targetField) || targetField == null)
            {
                inj_snackbar.Add("当前字段缺少目标信息，无法生成 LLM 建议。", Severity.Warning);
                return;
            }

            targetKey = GetMappingKey(selectedField);
            _llmFocusedFieldName = string.IsNullOrWhiteSpace(selectedField.DisplayName)
                ? selectedField.FieldId
                : $"{selectedField.DisplayName}（{selectedField.FieldId}）";
        }
        else
        {
            inj_snackbar.Add("当前字段缺少目标信息，无法生成 LLM 建议。", Severity.Warning);
            return;
        }

        _llmSuggestionMode = LlmSuggestionMode.SingleField;
        _llmFocusedMappingKey = targetKey;
        _llmLoading = true;
        _llmError = null;
        _llmSuggestions = [];
        _suggestionPanelExpanded = false;

        try
        {
            var (suggestions, rawResponse) = await LlmService.SuggestMappingsWithRawAsync(_parsedJson, [targetField], GetLlmScopeHintText(), CancellationToken, GetMainRecordArrayPath());
            var llmTargetKey = GetMappingKey(targetField.Category, targetField.FieldId, targetField.CardId);

            _llmSuggestions = suggestions
                .Where(s => GetMappingKey(s.MappingTarget, s.TargetField, s.CardId) == llmTargetKey)
                .GroupBy(GetSuggestionIdentityKey, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(s => s.Confidence).First())
                .OrderByDescending(s => s.Confidence)
                .Take(5)
                .Select((s, index) =>
                {
                    var normalizedArrayPath = NormalizeEditableSubCardArrayPathValue(s.ArrayPath);
                    return new LlmSuggestionItem
                    {
                        SourcePath = NormalizeSuggestionSourcePath(s.MappingTarget, s.CardId, normalizedArrayPath, s.SourcePath),
                        TargetField = s.TargetField,
                        DisplayName = targetField.DisplayName,
                        MappingTarget = s.MappingTarget,
                        Confidence = s.Confidence,
                        Reason = s.Reason,
                        DictCode = s.DictCode,
                        DefaultValue = s.DefaultValue,
                        CardId = s.CardId,
                        CardName = targetField.CardName,
                        ArrayPath = normalizedArrayPath,
                        IsSelected = index == 0,
                    };
                })
                .ToList();

            if (_llmSuggestions.Count == 0)
            {
                _llmDialogVisible = false;
                var preview = rawResponse.Length > 200 ? rawResponse[..200] + "..." : rawResponse;
                inj_snackbar.Add($"LLM 未返回当前字段的可用建议。原始返回: {preview}", Severity.Warning);
                return;
            }

            _llmDialogVisible = true;
            _suggestionPanelExpanded = false;
            inj_snackbar.Add($"LLM 为当前字段生成 {_llmSuggestions.Count} 条建议。", Severity.Success);
        }
        catch (Exception ex)
        {
            _llmError = $"LLM 调用失败: {ex.Message}";
        }
        finally
        {
            _llmLoading = false;
        }
    }

    private bool TryBuildTargetFieldInfo(WizardMappingRow row, out TargetFieldInfo? targetField)
    {
        if (string.IsNullOrWhiteSpace(row.TargetField))
        {
            targetField = null;
            return false;
        }

        targetField = new TargetFieldInfo
        {
            FieldId = row.TargetField,
            DisplayName = string.IsNullOrWhiteSpace(row.TargetFieldDisplayName) ? row.TargetField : row.TargetFieldDisplayName,
            DataType = ResolveTargetFieldDataType(row.MappingTarget, row.TargetField),
            Category = row.MappingTarget,
            SemanticHint = ResolveTargetFieldSemanticHint(row.MappingTarget, row.TargetField),
            CardId = row.MappingTarget == MappingTarget.SubCard ? row.CardId : null,
            CardName = row.MappingTarget == MappingTarget.SubCard ? row.CardName : null,
        };

        return true;
    }

    private bool TryBuildTargetFieldInfo(TargetFieldDescriptor field, out TargetFieldInfo? targetField)
    {
        if (string.IsNullOrWhiteSpace(field.FieldId))
        {
            targetField = null;
            return false;
        }

        targetField = new TargetFieldInfo
        {
            FieldId = field.FieldId,
            DisplayName = string.IsNullOrWhiteSpace(field.DisplayName) ? field.FieldId : field.DisplayName,
            DataType = field.DataType ?? ResolveTargetFieldDataType(field.MappingTarget, field.FieldId),
            Category = field.MappingTarget,
            SemanticHint = string.IsNullOrWhiteSpace(field.SemanticHint)
                ? ResolveTargetFieldSemanticHint(field.MappingTarget, field.FieldId)
                : field.SemanticHint,
            CardId = field.MappingTarget == MappingTarget.SubCard ? field.CardId : null,
            CardName = field.MappingTarget == MappingTarget.SubCard ? field.CardName : null,
        };

        return true;
    }

    private static string GetSuggestionIdentityKey(MappingSuggestion suggestion)
    {
        var arrayPath = SubCardPathHelper.NormalizeArrayContainerPath(suggestion.ArrayPath);
        var sourcePath = SubCardPathHelper.NormalizeArrayPath(suggestion.SourcePath);
        return $"{arrayPath}|{sourcePath}";
    }

    private string ResolveTargetFieldDataType(MappingTarget mappingTarget, string targetField)
    {
        return mappingTarget switch
        {
            MappingTarget.Patient => GetPatientFieldDefinition(targetField)?.DataType ?? "",
            MappingTarget.Event => EventTargetFields.FirstOrDefault(f => f.FieldId == targetField)?.DataType ?? "",
            MappingTarget.Question or MappingTarget.SubCard => _questionLookup.TryGetValue(targetField, out var q) ? q.DataType ?? "" : "",
            _ => "",
        };
    }

    private string? ResolveTargetFieldSemanticHint(MappingTarget mappingTarget, string targetField)
    {
        return mappingTarget switch
        {
            MappingTarget.Question or MappingTarget.SubCard => _questionLookup.TryGetValue(targetField, out var q)
                ? BuildQuestionSemanticHint(q)
                : null,
            _ => null,
        };
    }

    private Task<List<TargetFieldInfo>> BuildTargetFieldsAsync()
    {
        IEnumerable<TargetFieldDescriptor> sourceFields = _selectedGenerateTarget == MappingTarget.Question
            ? GetCurrentQuestionScopeTargetFields()
            : GetCurrentTargetFields(applySearchFilters: false);

        var fields = sourceFields
            .Select(f => new TargetFieldInfo
            {
                FieldId = f.FieldId,
                DisplayName = f.DisplayName,
                DataType = f.DataType ?? "",
                Category = f.MappingTarget,
                SemanticHint = string.IsNullOrWhiteSpace(f.SemanticHint)
                    ? ResolveTargetFieldSemanticHint(f.MappingTarget, f.FieldId)
                    : f.SemanticHint,
                CardId = f.CardId,
                CardName = f.CardName,
            })
            .ToList();

        return Task.FromResult(fields);
    }

    private IEnumerable<TargetFieldDescriptor> GetCurrentQuestionScopeTargetFields()
    {
        if (!IsQuestionWorkbenchMode || SelectedScopeCard == null || ActiveForm == null)
        {
            return Enumerable.Empty<TargetFieldDescriptor>();
        }

        var subCardAncestor = FindSubCardAncestor(SelectedScopeCard, ActiveForm.Cards);
        return EnumerateQuestionFieldsForNode(SelectedScopeCard, subCardAncestor, includeDescendants: false);
    }

    private CardNode? GetCurrentSubCardGroupCard()
    {
        if (!IsQuestionWorkbenchMode || ActiveForm == null)
        {
            return null;
        }

        if (SelectedScopeCard != null && IsSubCardNode(SelectedScopeCard))
        {
            return SelectedScopeCard;
        }

        if (SelectedQuestionField?.MappingTarget == MappingTarget.SubCard && SelectedQuestionField.CardId.HasValue)
        {
            return FindCardNode(ActiveForm.Cards, SelectedQuestionField.CardId.Value);
        }

        return null;
    }

    private IEnumerable<TargetFieldDescriptor> GetCurrentSubCardGroupFields()
    {
        var subCard = CurrentSubCardGroupCard;
        if (subCard == null)
        {
            return Enumerable.Empty<TargetFieldDescriptor>();
        }

        return EnumerateQuestionFieldsForNode(subCard, subCard, includeDescendants: false);
    }

    private List<SuggestionGroupViewModel> GetCurrentSuggestionGroups()
    {
        if (_llmSuggestions.Count == 0)
        {
            return [];
        }

        if (_llmSuggestionMode == LlmSuggestionMode.SingleField)
        {
            return
            [
                new SuggestionGroupViewModel
                {
                    Key = "single",
                    Title = _llmFocusedFieldName ?? "当前字段",
                    Subtitle = "单字段候选建议",
                    CardId = _llmSuggestions.FirstOrDefault()?.CardId,
                    ArrayPath = ResolveSuggestionGroupArrayPath(_llmSuggestions),
                    IsSubCardGroup = _llmSuggestions.Any(s => s.MappingTarget == MappingTarget.SubCard),
                    HasArrayPathConflict = HasSuggestionArrayPathConflict(_llmSuggestions),
                    HasMissingArrayPath = HasSuggestionGroupMissingArrayPath(_llmSuggestions),
                    Color = ActiveRow != null
                        ? GetTargetColor(ActiveRow.MappingTarget)
                        : SelectedQuestionField != null
                            ? GetTargetColor(SelectedQuestionField.MappingTarget)
                            : Color.Success,
                    SortOrder = 0,
                    Items = _llmSuggestions.OrderByDescending(s => s.Confidence).ToList(),
                }
            ];
        }

        return _llmSuggestions
            .GroupBy(GetSuggestionGroupKey)
            .Select(g =>
            {
                var items = g.OrderByDescending(x => x.Confidence).ToList();
                var first = items[0];
                return new SuggestionGroupViewModel
                {
                    Key = g.Key,
                    Title = GetSuggestionGroupTitle(first),
                    Subtitle = GetSuggestionGroupSubtitle(first),
                    CardId = first.CardId,
                    ArrayPath = ResolveSuggestionGroupArrayPath(items),
                    IsSubCardGroup = first.MappingTarget == MappingTarget.SubCard,
                    HasArrayPathConflict = HasSuggestionArrayPathConflict(items),
                    HasMissingArrayPath = HasSuggestionGroupMissingArrayPath(items),
                    Color = first.MappingTarget == MappingTarget.SubCard ? Color.Warning : Color.Success,
                    SortOrder = first.MappingTarget == MappingTarget.SubCard ? 1 : 0,
                    Items = items,
                };
            })
            .OrderBy(g => g.SortOrder)
            .ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string GetSuggestionGroupKey(LlmSuggestionItem item)
    {
        if (_llmSuggestionMode == LlmSuggestionMode.SingleField)
        {
            return "single";
        }

        return item.MappingTarget == MappingTarget.SubCard
            ? $"subcard:{item.CardId}"
            : "card:direct";
    }

    private string GetSuggestionGroupTitle(LlmSuggestionItem item)
    {
        if (_llmSuggestionMode == LlmSuggestionMode.SingleField)
        {
            return _llmFocusedFieldName ?? "当前字段";
        }

        if (_selectedGenerateTarget != MappingTarget.Question)
        {
            return GetTargetDisplayText(item.MappingTarget);
        }

        return item.MappingTarget == MappingTarget.SubCard
            ? item.CardName ?? "未命名子卡"
            : _selectedTreeCardName ?? "当前卡片";
    }

    private string GetSuggestionGroupSubtitle(LlmSuggestionItem item)
    {
        if (_selectedGenerateTarget != MappingTarget.Question)
        {
            return "当前范围建议";
        }

        return item.MappingTarget == MappingTarget.SubCard
            ? "子卡问题建议"
            : "当前卡片问题建议";
    }

    private static int GetSuggestionGroupSelectedCount(SuggestionGroupViewModel group) =>
        group.Items.Count(s => s.IsSelected);

    private string? ResolveSuggestionDisplayArrayPath(LlmSuggestionItem suggestion)
    {
        if (suggestion.MappingTarget != MappingTarget.SubCard || !suggestion.CardId.HasValue)
        {
            return NormalizeEditableSubCardArrayPathValue(suggestion.ArrayPath);
        }

        if (!string.IsNullOrWhiteSpace(suggestion.ArrayPath))
        {
            return NormalizeEditableSubCardArrayPathValue(suggestion.ArrayPath);
        }

        if (TryInferSubCardArrayPathFromSourcePath(suggestion.SourcePath, out var inferredArrayPath))
        {
            return inferredArrayPath;
        }

        return GetSubCardArrayPath(suggestion.CardId.Value);
    }

    private string? ResolveSuggestionGroupArrayPath(IEnumerable<LlmSuggestionItem> suggestions)
    {
        var items = suggestions.ToList();
        if (items.Count == 0 || items.All(s => s.MappingTarget != MappingTarget.SubCard))
        {
            return null;
        }

        var candidatePaths = ResolveSuggestionGroupArrayPathCandidates(items);
        var cardId = items.Select(s => s.CardId).FirstOrDefault(id => id.HasValue);
        if (candidatePaths.Count == 1
            && SubCardPathHelper.IsMainRecordContainerPath(candidatePaths[0])
            && cardId.HasValue)
        {
            var existingArrayPath = GetSubCardArrayPath(cardId.Value);
            if (!string.IsNullOrWhiteSpace(existingArrayPath)
                && !SubCardPathHelper.IsMainRecordContainerPath(existingArrayPath))
            {
                return existingArrayPath;
            }
        }

        if (candidatePaths.Count == 1)
        {
            return candidatePaths[0];
        }

        return cardId.HasValue ? GetSubCardArrayPath(cardId.Value) : null;
    }

    private List<string> ResolveSuggestionGroupArrayPathCandidates(IEnumerable<LlmSuggestionItem> suggestions)
    {
        var candidatePaths = suggestions
            .Where(s => s.MappingTarget == MappingTarget.SubCard)
            .Select(ResolveSuggestionDisplayArrayPath)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return candidatePaths.Any(path => !SubCardPathHelper.IsMainRecordContainerPath(path))
            ? candidatePaths
                .Where(path => !SubCardPathHelper.IsMainRecordContainerPath(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : candidatePaths;
    }

    private bool HasSuggestionArrayPathConflict(IEnumerable<LlmSuggestionItem> suggestions)
    {
        var items = suggestions
            .Where(s => s.MappingTarget == MappingTarget.SubCard)
            .ToList();
        if (items.Count == 0)
        {
            return false;
        }

        var candidatePaths = ResolveSuggestionGroupArrayPathCandidates(items);

        return candidatePaths.Count > 1;
    }

    private bool HasSuggestionGroupMissingArrayPath(IEnumerable<LlmSuggestionItem> suggestions)
    {
        var items = suggestions
            .Where(s => s.MappingTarget == MappingTarget.SubCard)
            .ToList();
        if (items.Count == 0)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(ResolveSuggestionGroupArrayPath(items));
    }

    private bool CanApplySuggestions(IEnumerable<LlmSuggestionItem> suggestions)
    {
        return TryValidateSelectedSuggestions(suggestions.ToList(), out _);
    }

    private bool TryValidateSelectedSuggestions(List<LlmSuggestionItem> suggestions, out string? message)
    {
        message = null;
        if (suggestions.Count == 0)
        {
            message = "请先选择至少一条建议。";
            return false;
        }

        foreach (var group in suggestions
                     .Where(s => s.MappingTarget == MappingTarget.SubCard && s.CardId.HasValue)
                     .GroupBy(s => s.CardId!.Value))
        {
            var groupItems = group.ToList();
            var candidatePaths = ResolveSuggestionGroupArrayPathCandidates(groupItems);

            if (candidatePaths.Count > 1)
            {
                var cardName = groupItems.Select(s => s.CardName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "当前子卡";
                message = $"子卡【{cardName}】的 LLM 建议返回了多个不同的 ArrayPath，请先重新生成或缩小范围。";
                return false;
            }

            var resolvedArrayPath = ResolveSuggestionGroupArrayPath(groupItems);

            if (string.IsNullOrWhiteSpace(resolvedArrayPath))
            {
                var cardName = groupItems.Select(s => s.CardName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "当前子卡";
                message = $"子卡【{cardName}】缺少 ArrayPath，当前这组建议不能直接应用。";
                return false;
            }
        }

        return true;
    }

    private void SetSuggestionGroupSelection(string groupKey, bool value)
    {
        foreach (var suggestion in _llmSuggestions.Where(s => GetSuggestionGroupKey(s) == groupKey))
        {
            suggestion.IsSelected = value;
        }
    }

    private void AcceptAllSuggestions()
    {
        foreach (var suggestion in _llmSuggestions)
        {
            suggestion.IsSelected = true;
        }
    }

    private void RejectAllSuggestions()
    {
        foreach (var suggestion in _llmSuggestions)
        {
            suggestion.IsSelected = false;
        }
    }

    private void AcceptHighConfidenceSuggestions()
    {
        foreach (var suggestion in _llmSuggestions)
        {
            suggestion.IsSelected = suggestion.Confidence >= 0.8;
        }
    }

    private void ToggleAllSuggestions(bool value)
    {
        foreach (var suggestion in _llmSuggestions)
        {
            suggestion.IsSelected = value;
        }
    }

    #pragma warning disable CS0162
    private void ApplySelectedSuggestions()
    {
        ApplySuggestionsCore(_llmSuggestions.Where(s => s.IsSelected).ToList());
    }

    private void ApplySuggestionGroup(string groupKey)
    {
        var scopedSuggestions = _llmSuggestions
            .Where(s => s.IsSelected && GetSuggestionGroupKey(s) == groupKey)
            .ToList();
        ApplySuggestionsCore(scopedSuggestions);
    }

    private void ApplySuggestionsCore(List<LlmSuggestionItem> selectedSuggestions)
    {
        if (selectedSuggestions.Count == 0)
        {
            return;
        }

        if (!TryValidateSelectedSuggestions(selectedSuggestions, out var validationMessage))
        {
            inj_snackbar.Add(validationMessage ?? "当前建议无法应用。", Severity.Warning);
            return;
        }

        var addedRows = new List<WizardMappingRow>();
        var updatedRows = new List<WizardMappingRow>();
        var existingKeys = _mappingRows.Select(GetMappingKey).ToHashSet();
        var skipped = 0;
        IEnumerable<LlmSuggestionItem> suggestionsToApply = selectedSuggestions;
        var subCardArrayPaths = selectedSuggestions
            .Where(s => s.MappingTarget == MappingTarget.SubCard && s.CardId.HasValue)
            .GroupBy(s => s.CardId!.Value)
            .ToDictionary(g => g.Key, g => ResolveSuggestionGroupArrayPath(g));

        if (_llmSuggestionMode == LlmSuggestionMode.SingleField)
        {
            suggestionsToApply = selectedSuggestions
                .GroupBy(s => GetMappingKey(s.MappingTarget, s.TargetField, s.CardId))
                .Select(g => g.OrderByDescending(s => s.Confidence).First());
        }

        foreach (var suggestion in suggestionsToApply)
        {
            var key = GetMappingKey(suggestion.MappingTarget, suggestion.TargetField, suggestion.CardId);
            if (existingKeys.Contains(key))
            {
                if (_llmSuggestionMode != LlmSuggestionMode.SingleField)
                {
                    skipped++;
                    continue;
                }

                var existingRow = _mappingRows.FirstOrDefault(r => GetMappingKey(r) == key);
                if (existingRow == null)
                {
                    skipped++;
                    continue;
                }

                var existingResolvedArrayPath = ResolveSuggestionArrayPath(suggestion, subCardArrayPaths);
                existingRow.SourcePath = NormalizeSuggestionSourcePath(suggestion.MappingTarget, suggestion.CardId, existingResolvedArrayPath, suggestion.SourcePath);
                existingRow.TargetFieldDisplayName = suggestion.DisplayName;
                existingRow.DictCode = suggestion.DictCode;
                existingRow.DefaultValue = suggestion.DefaultValue;
                existingRow.CardId = suggestion.CardId;
                existingRow.CardName = suggestion.CardName;
                existingRow.ArrayPath = existingResolvedArrayPath;
                existingRow.Origin = "llm";
                existingRow.IsEnabled = true;
                SyncSubCardArrayPath(existingRow);
                ValidateMappingRowPath(existingRow);
                updatedRows.Add(existingRow);
                continue;
            }

            existingKeys.Add(key);
            var resolvedArrayPath = ResolveSuggestionArrayPath(suggestion, subCardArrayPaths);
            var row = new WizardMappingRow
            {
                MappingTarget = suggestion.MappingTarget,
                SourcePath = NormalizeSuggestionSourcePath(suggestion.MappingTarget, suggestion.CardId, resolvedArrayPath, suggestion.SourcePath),
                TargetField = suggestion.TargetField,
                TargetFieldDisplayName = suggestion.DisplayName,
                DictCode = suggestion.DictCode,
                DefaultValue = suggestion.DefaultValue,
                CardId = suggestion.CardId,
                CardName = suggestion.CardName,
                ArrayPath = resolvedArrayPath,
                Origin = "llm",
                IsEnabled = true,
            };

            SyncSubCardArrayPath(row);
            ValidateMappingRowPath(row);
            _mappingRows.Add(row);
            addedRows.Add(row);
        }

        _llmSuggestions.RemoveAll(selectedSuggestions.Contains);
        _llmDialogVisible = false;
        _suggestionPanelExpanded = _llmSuggestions.Count > 0;

        if (addedRows.Count > 0)
        {
            _activeMappingKey = GetMappingKey(addedRows[0]);
        }
        else if (updatedRows.Count > 0)
        {
            _activeMappingKey = GetMappingKey(updatedRows[0]);
        }

        PublishSuggestionApplyResult(addedRows.Count, updatedRows.Count, skipped);
        return;

        var message = $"已应用 {addedRows.Count} 条映射";
        if (skipped > 0)
        {
            message += $"，跳过 {skipped} 条重复项";
        }

        inj_snackbar.Add(message, Severity.Success);
    }

    #pragma warning restore CS0162
    private void PublishSuggestionApplyResult(int addedCount, int updatedCount, int skippedCount)
    {
        var appliedCount = addedCount + updatedCount;
        var message = $"已应用 {appliedCount} 条映射";

        if (updatedCount > 0)
        {
            message += $"，更新 {updatedCount} 条";
        }

        if (addedCount > 0)
        {
            message += $"，新增 {addedCount} 条";
        }

        if (skippedCount > 0)
        {
            message += $"，跳过 {skippedCount} 条重复项";
        }

        inj_snackbar.Add(message, Severity.Success);
    }

    private string GetSuggestionTitle()
    {
        var scope = GetCurrentScopeText();
        return $"LLM 建议 - {scope}（{_llmSuggestions.Count} 条）";
    }

    private string GetSuggestionPanelTitle()
    {
        var scope = _selectedGenerateTarget == null ? "当前范围" : GetCurrentScopeText();
        return $"LLM 自动生成建议 - {scope}";
    }

    private string GetSuggestionDialogTitle() => $"LLM 建议确认（{_llmSuggestions.Count} 条）";

    private string GetSuggestionScopeText()
    {
        if (_selectedConfig == null)
        {
            return GetCurrentScopeText();
        }

        return $"{_selectedConfig.TranCode} / {GetCurrentScopeText()}";
    }

    private string GetLlmScopeHintText()
    {
        var scope = GetSuggestionScopeText();
        if (!string.IsNullOrWhiteSpace(GetMainRecordArrayPath()))
        {
            scope += $"；已配置主记录路径 {GetMainRecordArrayPath()}，非 SubCard 字段请优先输出主记录内相对路径，如需取根级字段请以 $. 开头";
        }

        if (_llmSuggestionMode != LlmSuggestionMode.SingleField || string.IsNullOrWhiteSpace(_llmFocusedFieldName))
        {
            return scope;
        }

        return $"{scope} / 当前字段 {_llmFocusedFieldName}";
    }

    private string GetSuggestionDialogHintText() => "勾选后仅应用到当前接口，点击保存映射后才会写入数据库。";

    private string GetActiveSuggestionDialogTitle() =>
        IsSingleFieldSuggestionMode
            ? $"LLM 单字段建议确认（{_llmSuggestions.Count} 条）"
            : GetSuggestionDialogTitle();

    private string GetActiveSuggestionTargetText() => _llmFocusedFieldName ?? string.Empty;

    private string GetActiveSuggestionDialogHintText() =>
        IsSingleFieldSuggestionMode
            ? "当前模式只会更新这个字段，勾选后点击“应用到当前接口”即可替换该字段的映射建议。"
            : GetSuggestionDialogHintText();

    private string GetSuggestionCategoryText(LlmSuggestionItem item) =>
        item.MappingTarget == MappingTarget.SubCard ? "Question（子卡）" : GetTargetDisplayText(item.MappingTarget);

    private string GetSuggestionValueHint(LlmSuggestionItem item)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(item.DictCode))
        {
            parts.Add($"字典:{item.DictCode}");
        }

        if (!string.IsNullOrWhiteSpace(item.DefaultValue))
        {
            parts.Add($"默认:{item.DefaultValue}");
        }

        return parts.Count == 0 ? "-" : string.Join(" / ", parts);
    }

    private string GetSuggestionGroupArrayPathText(SuggestionGroupViewModel group)
    {
        if (!IsSubCardSuggestionGroup(group))
        {
            return "当前节点建议";
        }

        if (HasSuggestionGroupArrayPathConflict(group))
        {
            return "ArrayPath 冲突";
        }

        var arrayPath = ResolveSuggestionGroupArrayPath(GetSuggestionGroupContextItems(group));
        return string.IsNullOrWhiteSpace(arrayPath)
            ? "ArrayPath 缺失"
            : $"ArrayPath: {arrayPath}";
    }

    private Color GetSuggestionGroupArrayPathColor(SuggestionGroupViewModel group)
    {
        if (!IsSubCardSuggestionGroup(group))
        {
            return Color.Info;
        }

        if (HasSuggestionGroupArrayPathConflict(group))
        {
            return Color.Error;
        }

        return HasSuggestionGroupMissingArrayPath(group) ? Color.Warning : Color.Success;
    }

    private bool CanApplySuggestionGroup(SuggestionGroupViewModel group) =>
        GetSuggestionGroupSelectedCount(group) > 0 && CanApplySuggestions(group.Items.Where(s => s.IsSelected));

    private static bool IsSubCardSuggestionGroup(SuggestionGroupViewModel group) =>
        group.Items.Any(s => s.MappingTarget == MappingTarget.SubCard);

    private List<LlmSuggestionItem> GetSuggestionGroupContextItems(SuggestionGroupViewModel group)
    {
        var selectedItems = group.Items.Where(s => s.IsSelected).ToList();
        return selectedItems.Count > 0 ? selectedItems : group.Items;
    }

    private bool HasSuggestionGroupArrayPathConflict(SuggestionGroupViewModel group) =>
        HasSuggestionArrayPathConflict(GetSuggestionGroupContextItems(group));

    private bool HasSuggestionGroupMissingArrayPath(SuggestionGroupViewModel group) =>
        HasSuggestionGroupMissingArrayPath(GetSuggestionGroupContextItems(group));

    private async Task OnLlmDialogVisibleChanged(bool value)
    {
        _llmDialogVisible = value;
        if (value || _llmSuggestions.Count == 0)
        {
            return;
        }

        await Task.Yield();
        DiscardSuggestions();
    }

    private void DiscardSuggestions()
    {
        _llmDialogVisible = false;
        _llmSuggestions = [];
        _llmError = null;
        _suggestionPanelExpanded = false;
    }

    private static bool HasSupplementalConfiguration(WizardMappingRow row) =>
        !string.IsNullOrWhiteSpace(row.DictCode)
        || EsbFieldMapping.NormalizeDictMatchMode(row.DictMatchMode) != EsbFieldMapping.DefaultDictMatchMode
        || !string.IsNullOrWhiteSpace(row.ValueExpression)
        || row.IsRequired
        || !row.IsEnabled
        || row.FilterRules.Count > 0;

    private void RemoveRowIfEmpty(WizardMappingRow row)
    {
        if (HasValueSource(row) || HasSupplementalConfiguration(row))
        {
            return;
        }

        var mappingKey = GetMappingKey(row);
        _mappingRows.Remove(row);
        if (string.Equals(_activeMappingKey, mappingKey, StringComparison.OrdinalIgnoreCase))
        {
            _activeMappingKey = FindCurrentTargetField(mappingKey) == null ? null : mappingKey;
        }

        if (string.Equals(_pathPickTargetKey, mappingKey, StringComparison.OrdinalIgnoreCase))
        {
            ClearPathPickState();
        }
    }

    private WizardMappingRow? GetQuestionFieldRow(TargetFieldDescriptor field) => FindMappingRow(field);

    private string GetQuestionFieldSourcePath(TargetFieldDescriptor field)
    {
        var row = GetQuestionFieldRow(field);
        return row == null
            ? ""
            : GetDisplaySourcePath(field.MappingTarget, field.CardId, row.SourcePath, GetQuestionFieldArrayPath(field));
    }

    private string? GetQuestionFieldDictCode(TargetFieldDescriptor field) => GetQuestionFieldRow(field)?.DictCode;

    private string GetQuestionFieldDictMatchMode(TargetFieldDescriptor field) =>
        EsbFieldMapping.NormalizeDictMatchMode(GetQuestionFieldRow(field)?.DictMatchMode);

    private IEnumerable<string> GetQuestionFieldDictSuggestedSourceValues(TargetFieldDescriptor field)
    {
        if (_questionLookup.TryGetValue(field.FieldId, out var question) && question.Options.Count > 0)
        {
            return question.Options;
        }

        return [];
    }

    private string? GetQuestionFieldDefaultValue(TargetFieldDescriptor field) => GetQuestionFieldRow(field)?.DefaultValue;

    private string? GetQuestionFieldValueExpression(TargetFieldDescriptor field) => GetQuestionFieldRow(field)?.ValueExpression;

    private bool GetQuestionFieldIsRequired(TargetFieldDescriptor field) => GetQuestionFieldRow(field)?.IsRequired ?? false;

    private bool GetQuestionFieldIsEnabled(TargetFieldDescriptor field) => GetQuestionFieldRow(field)?.IsEnabled ?? true;

    private string GetQuestionFieldArrayPath(TargetFieldDescriptor field)
    {
        if (field.MappingTarget != MappingTarget.SubCard || !field.CardId.HasValue)
        {
            return "";
        }

        var rowArrayPath = GetQuestionFieldRow(field)?.ArrayPath;
        return string.IsNullOrWhiteSpace(rowArrayPath)
            ? GetSubCardArrayPath(field.CardId.Value)
            : rowArrayPath;
    }

    private List<EsbFilterRule> GetQuestionFieldFilterRules(TargetFieldDescriptor field) => GetQuestionFieldRow(field)?.FilterRules ?? [];

    private static string GetSubCardFilterKey(Guid cardId) => $"subcard-filter:{cardId}";

    private List<EsbFilterRule> GetSubCardFilterRules(Guid cardId) =>
        _subCardFilterRulesByCardId.TryGetValue(cardId, out var rules) ? rules : [];

    private Task OnSubCardFilterRulesChanged(Guid cardId, List<EsbFilterRule> rules)
    {
        if (rules.Count == 0)
        {
            _subCardFilterRulesByCardId.Remove(cardId);
        }
        else
        {
            _subCardFilterRulesByCardId[cardId] = rules;
        }

        return Task.CompletedTask;
    }

    private bool IsSubCardFilterExpanded(Guid cardId) =>
        _expandedSubCardFilterCards.Contains(cardId);

    private void ToggleSubCardFilterExpanded(Guid cardId)
    {
        if (!_expandedSubCardFilterCards.Add(cardId))
        {
            _expandedSubCardFilterCards.Remove(cardId);
        }
    }

    private void AddSubCardFilterRule(Guid cardId)
    {
        if (!_subCardFilterRulesByCardId.TryGetValue(cardId, out var rules))
        {
            rules = [];
            _subCardFilterRulesByCardId[cardId] = rules;
        }

        rules.Add(new EsbFilterRule { Operator = "eq", RuleGroup = 1, IsEnabled = true });
        _expandedSubCardFilterCards.Add(cardId);
    }

    private bool IsQuestionFieldPathValid(TargetFieldDescriptor field) => GetQuestionFieldRow(field)?.IsPathValid ?? true;

    private bool IsPathPickActive(TargetFieldDescriptor field) =>
        _pathPickMode == PathPickMode.ActiveRow
        && _pathPickTargetKind == PathPickTargetKind.SourcePath
        && string.Equals(_pathPickTargetKey, GetMappingKey(field), StringComparison.OrdinalIgnoreCase);

    private int GetQuestionFieldActiveFilterPickIndex(TargetFieldDescriptor field) =>
        _pathPickMode == PathPickMode.ActiveRow
        && _pathPickTargetKind == PathPickTargetKind.FilterRule
        && string.Equals(_pathPickTargetKey, GetMappingKey(field), StringComparison.OrdinalIgnoreCase)
            ? _pathPickRuleIndex
            : -1;

    private int GetSubCardFilterActivePickIndex(Guid cardId) =>
        _pathPickMode == PathPickMode.ActiveRow
        && _pathPickTargetKind == PathPickTargetKind.SubCardFilterRule
        && _pathPickArrayCardId == cardId
            ? _pathPickRuleIndex
            : -1;

    private bool IsQuestionFieldArrayPathPickActive(TargetFieldDescriptor field) =>
        _pathPickMode == PathPickMode.ActiveRow
        && _pathPickTargetKind == PathPickTargetKind.ArrayPath
        && field.CardId.HasValue
        && _pathPickArrayCardId == field.CardId;

    private bool IsSelectedSubCardArrayPathPickActive(Guid cardId) =>
        _pathPickMode == PathPickMode.ActiveRow
        && _pathPickTargetKind == PathPickTargetKind.ArrayPath
        && _pathPickArrayCardId == cardId;

    private string? GetActiveStandaloneMappingKey()
    {
        if (ActiveRow != null)
        {
            return GetMappingKey(ActiveRow);
        }

        return SelectedStandaloneField == null ? null : GetMappingKey(SelectedStandaloneField);
    }

    private string GetActiveStandaloneSourcePath() => ActiveRow == null ? "" : GetDisplaySourcePath(ActiveRow);

    private string? GetActiveStandaloneDictCode() => ActiveRow?.DictCode;

    private string GetActiveStandaloneDictMatchMode() =>
        EsbFieldMapping.NormalizeDictMatchMode(ActiveRow?.DictMatchMode);

    private string? GetActiveStandaloneDefaultValue() => ActiveRow?.DefaultValue;

    private string? GetActiveStandaloneValueExpression() => ActiveRow?.ValueExpression;

    private bool GetActiveStandaloneIsRequired() => ActiveRow?.IsRequired ?? false;

    private bool GetActiveStandaloneIsEnabled() => ActiveRow?.IsEnabled ?? true;

    private List<EsbFilterRule> GetActiveStandaloneFilterRules() => ActiveRow?.FilterRules ?? [];

    private bool IsActiveStandalonePathValid() => ActiveRow?.IsPathValid ?? true;

    private bool ShowActiveStandalonePendingHint() => ActiveRow != null && !HasValueSource(ActiveRow);

    private string GetActiveStandalonePendingHintText() =>
        ActiveRow?.FilterRules.Count > 0
            ? "当前仅配置了过滤条件，还需补充源路径或默认值后才能保存。"
            : "当前映射还缺少源路径或默认值，暂时无法保存。";

    private bool IsActiveRowSourcePathPick() =>
        _pathPickMode == PathPickMode.ActiveRow
        && _pathPickTargetKind == PathPickTargetKind.SourcePath
        && !string.IsNullOrWhiteSpace(GetActiveStandaloneMappingKey())
        && string.Equals(_pathPickTargetKey, GetActiveStandaloneMappingKey(), StringComparison.OrdinalIgnoreCase);

    private string? GetEffectiveArrayPath(Guid? cardId, string? arrayPath, string? sourcePath = null)
    {
        if (!string.IsNullOrWhiteSpace(arrayPath))
        {
            return ExpandSubCardArrayPathToRoot(arrayPath);
        }

        if (TryInferSubCardArrayPathFromSourcePath(sourcePath, out var parsedArrayPath))
        {
            return parsedArrayPath;
        }

        if (cardId.HasValue
            && _parsedJson is JArray rootArray
            && rootArray.Count > 0)
        {
            var normalizedSourcePath = SubCardPathHelper.NormalizeArrayPath(sourcePath);
            if (!string.IsNullOrWhiteSpace(normalizedSourcePath)
                && !SubCardPathHelper.IsAbsoluteJsonPath(normalizedSourcePath)
                && !SubCardPathHelper.HasArrayWildcard(normalizedSourcePath)
                && MessageJsonHelper.SelectSampleToken(rootArray[0], normalizedSourcePath) != null)
            {
                return "$";
            }
        }

        return cardId.HasValue ? ExpandSubCardArrayPathToRoot(GetSubCardArrayPath(cardId.Value)) : null;
    }

    private string? ResolveSuggestionEffectiveArrayPath(LlmSuggestionItem suggestion)
    {
        if (suggestion.MappingTarget != MappingTarget.SubCard)
        {
            return SubCardPathHelper.NormalizeArrayContainerPath(suggestion.ArrayPath);
        }

        return GetEffectiveArrayPath(suggestion.CardId, suggestion.ArrayPath, suggestion.SourcePath);
    }

    private static bool IsRootSubCardPath(string? sourcePath, string? arrayPath)
        => SubCardPathHelper.IsRootScopedPath(sourcePath, arrayPath);

    private bool UsesRelativeSubCardPath(MappingTarget mappingTarget, Guid? cardId, string? arrayPath, string? sourcePath) =>
        mappingTarget == MappingTarget.SubCard
        && !string.IsNullOrWhiteSpace(sourcePath)
        && !IsAbsoluteJsonPath(sourcePath)
        && !IsMainRecordScopedPath(sourcePath)
        && !IsRootSubCardPath(sourcePath, GetEffectiveArrayPath(cardId, arrayPath, sourcePath));

    private bool UsesRelativeSubCardPath(WizardMappingRow row) =>
        UsesRelativeSubCardPath(row.MappingTarget, row.CardId, row.ArrayPath, row.SourcePath);

    private bool UsesRelativeSubCardPath(LlmSuggestionItem item) =>
        UsesRelativeSubCardPath(item.MappingTarget, item.CardId, item.ArrayPath, item.SourcePath);

    private bool NeedsSubCardArrayPath(WizardMappingRow row) =>
        UsesRelativeSubCardPath(row) && string.IsNullOrWhiteSpace(row.ArrayPath);

    private bool NeedsSubCardArrayPath(TargetFieldDescriptor field)
    {
        var row = GetQuestionFieldRow(field);
        return row != null && NeedsSubCardArrayPath(row);
    }

    private bool HasIncompleteSubCardContext(WizardMappingRow row) =>
        row.MappingTarget == MappingTarget.SubCard
        && HasValueSource(row)
        && (!row.CardId.HasValue || string.IsNullOrWhiteSpace(GetPendingSubCardArrayPath(row)));

    private bool IsSubCardArrayPathValid(string? arrayPath)
    {
        var effectiveArrayPath = ExpandSubCardArrayPathToRoot(arrayPath);
        if (string.IsNullOrWhiteSpace(effectiveArrayPath) || _parsedJson == null)
        {
            return false;
        }

        return SubCardPathHelper.IsSupportedSubCardContainer(
            SubCardPathHelper.ResolveSubCardContainer(_parsedJson, effectiveArrayPath));
    }

    private bool HasInvalidSubCardArrayPath(WizardMappingRow row)
    {
        if (_parsedJson == null || row.MappingTarget != MappingTarget.SubCard || !HasValueSource(row))
        {
            return false;
        }

        var pendingArrayPath = GetPendingSubCardArrayPath(row);
        return !string.IsNullOrWhiteSpace(pendingArrayPath) && !IsSubCardArrayPathValid(pendingArrayPath);
    }

    private bool HasInvalidSubCardArrayPath(Guid cardId)
    {
        if (_parsedJson == null || HasMixedSubCardArrayPaths(cardId))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(GetSubCardArrayPath(cardId))
            && !IsSubCardArrayPathValid(GetSubCardArrayPath(cardId));
    }

    private string GetSubCardArrayPathIssueText(Guid cardId)
    {
        var arrayPath = GetSubCardArrayPath(cardId);
        if (string.IsNullOrWhiteSpace(arrayPath))
        {
            return "容器路径未配置";
        }

        if (_parsedJson == null)
        {
            return "容器路径待校验";
        }

        var token = SubCardPathHelper.ResolveSubCardContainer(_parsedJson, ExpandSubCardArrayPathToRoot(arrayPath));
        return token switch
        {
            null => "容器路径未匹配",
            JArray _ => "容器路径已配置",
            JObject _ => "容器路径已配置",
            _ => "容器路径不是对象或数组",
        };
    }

    private static JToken? SafeSelectToken(JToken token, string path) =>
        SubCardPathHelper.SafeSelectToken(token, path);

    private JToken? ResolvePreviewToken(MappingTarget mappingTarget, Guid? cardId, string? arrayPath, string? sourcePath)
    {
        if (_parsedJson == null || string.IsNullOrWhiteSpace(sourcePath))
        {
            return null;
        }

        if (mappingTarget != MappingTarget.SubCard)
        {
            return ResolveStandalonePreviewToken(sourcePath);
        }

        var normalizedSourcePath = NormalizeSubCardSourcePathForPreview(sourcePath);
        if (SubCardPathHelper.IsAbsoluteJsonPath(normalizedSourcePath))
        {
            return MessageJsonHelper.ResolveSampleToken(_parsedJson, normalizedSourcePath);
        }

        return ResolveSubCardScopedPreviewToken(cardId, arrayPath, normalizedSourcePath, out _);
    }

    private string GetSampleValue(MappingTarget mappingTarget, Guid? cardId, string? arrayPath, string? sourcePath)
    {
        var value = ResolveSampleRawValue(mappingTarget, cardId, arrayPath, sourcePath, out var missingText);
        return value == null ? missingText : FormatPreviewValue(value, "(未匹配)");
    }

    private string GetSampleValue(string? sourcePath) =>
        GetSampleValue(MappingTarget.Patient, null, null, sourcePath);

    private string GetSampleValue(WizardMappingRow row) =>
        GetSampleValue(row.MappingTarget, row.CardId, row.ArrayPath, row.SourcePath);

    private string GetSampleValue(TargetFieldDescriptor field)
    {
        var row = GetQuestionFieldRow(field);
        return GetSampleValue(
            field.MappingTarget,
            field.CardId,
            row?.ArrayPath ?? (field.CardId.HasValue ? GetSubCardArrayPath(field.CardId.Value) : null),
            row?.SourcePath);
    }

    private string GetConvertedSampleText(WizardMappingRow? row)
    {
        if (row == null || string.IsNullOrWhiteSpace(row.ValueExpression))
        {
            return "";
        }

        return "；转换后：" + GetConvertedSampleValue(
            row.MappingTarget,
            row.CardId,
            row.ArrayPath,
            row.SourcePath,
            row.ValueExpression);
    }

    private string GetConvertedSampleText(TargetFieldDescriptor field)
    {
        var row = GetQuestionFieldRow(field);
        if (row == null || string.IsNullOrWhiteSpace(row.ValueExpression))
        {
            return "";
        }

        return "；转换后：" + GetConvertedSampleValue(
            field.MappingTarget,
            field.CardId,
            row.ArrayPath ?? (field.CardId.HasValue ? GetSubCardArrayPath(field.CardId.Value) : null),
            row.SourcePath,
            row.ValueExpression);
    }

    private string GetConvertedSampleValue(
        MappingTarget mappingTarget,
        Guid? cardId,
        string? arrayPath,
        string? sourcePath,
        string valueExpression)
    {
        var value = ResolveSampleRawValue(mappingTarget, cardId, arrayPath, sourcePath, out var missingText);
        if (value == null)
        {
            return missingText;
        }

        var convertedValue = FieldMappingExecutor.ApplyExpression(value, valueExpression);
        return FormatPreviewValue(convertedValue, "(空值)");
    }

    private string? ResolveSampleRawValue(
        MappingTarget mappingTarget,
        Guid? cardId,
        string? arrayPath,
        string? sourcePath,
        out string missingText)
    {
        missingText = "未选择源路径";
        if (_parsedJson == null || string.IsNullOrWhiteSpace(sourcePath))
        {
            return null;
        }

        var token = ResolvePreviewToken(mappingTarget, cardId, arrayPath, sourcePath);
        if (token != null)
        {
            return token.ToString();
        }

        if (mappingTarget == MappingTarget.SubCard
            && !SubCardPathHelper.IsAbsoluteJsonPath(NormalizeSubCardSourcePathForPreview(sourcePath))
            && !IsMainRecordScopedPath(NormalizeSubCardSourcePathForPreview(sourcePath))
            && string.IsNullOrWhiteSpace(GetEffectiveArrayPath(cardId, arrayPath, sourcePath)))
        {
            missingText = "(需先配置 ArrayPath)";
            return null;
        }

        missingText = "(未匹配)";
        return null;
    }

    private static string FormatPreviewValue(string? value, string nullText)
    {
        if (value == null)
        {
            return nullText;
        }

        if (value.Length == 0)
        {
            return "(空字符串)";
        }

        return value.Length > 80 ? value[..80] + "..." : value;
    }

    private string GetSuggestionPreviewArrayPath(LlmSuggestionItem item)
    {
        if (item.MappingTarget != MappingTarget.SubCard || !item.CardId.HasValue)
        {
            return SubCardPathHelper.NormalizeArrayContainerPath(item.ArrayPath);
        }

        var groupArrayPath = ResolveSuggestionContextArrayPath(item);
        if (!string.IsNullOrWhiteSpace(groupArrayPath))
        {
            return groupArrayPath;
        }

        var effectiveArrayPath = GetEffectiveArrayPath(item.CardId, item.ArrayPath, item.SourcePath);
        if (!string.IsNullOrWhiteSpace(effectiveArrayPath))
        {
            return effectiveArrayPath;
        }

        return GetSubCardArrayPath(item.CardId.Value);
    }

    private string? ResolveSuggestionContextArrayPath(LlmSuggestionItem item)
    {
        if (item.MappingTarget != MappingTarget.SubCard || !item.CardId.HasValue)
        {
            return ResolveSuggestionDisplayArrayPath(item);
        }

        var groupItems = _llmSuggestions
            .Where(s => GetSuggestionGroupKey(s) == GetSuggestionGroupKey(item))
            .ToList();
        var groupArrayPath = ResolveSuggestionGroupArrayPath(groupItems);
        return string.IsNullOrWhiteSpace(groupArrayPath)
            ? ResolveSuggestionDisplayArrayPath(item)
            : groupArrayPath;
    }

    private string GetSuggestionDisplaySourcePath(LlmSuggestionItem item) =>
        GetDisplaySourcePath(item.MappingTarget, item.CardId, item.SourcePath, ResolveSuggestionContextArrayPath(item));

    private string GetSuggestionSampleValue(LlmSuggestionItem item) =>
        GetSampleValue(item.MappingTarget, item.CardId, GetSuggestionPreviewArrayPath(item), item.SourcePath);

    private string GetPathScopeText(MappingTarget mappingTarget, Guid? cardId, string? arrayPath, string? sourcePath) =>
        mappingTarget == MappingTarget.SubCard
            ? (ResolveSubCardScopedPreviewToken(cardId, arrayPath, NormalizeSubCardSourcePathForPreview(sourcePath), out var resolvedFromMainRecord) != null
                ? (resolvedFromMainRecord ? "主记录内" : "数组项内")
                : (SubCardPathHelper.IsAbsoluteJsonPath(sourcePath)
                    ? "根路径"
                    : IsMainRecordScopedPath(sourcePath)
                        ? "主记录内"
                        : (UsesRelativeSubCardPath(mappingTarget, cardId, arrayPath, sourcePath) || SubCardPathHelper.HasArrayWildcard(sourcePath))
                            ? "数组项内"
                            : "根路径"))
            : TryBuildMainRecordScopedPath(sourcePath, out _)
                ? "主记录内"
                : "根路径";

    private void OnQuestionFieldSourcePathChanged(TargetFieldDescriptor field, string? value)
    {
        var currentArrayPath = GetQuestionFieldRow(field)?.ArrayPath
            ?? (field.CardId.HasValue ? GetSubCardArrayPath(field.CardId.Value) : null);
        var normalized = NormalizeEditableSourcePath(field.MappingTarget, field.CardId, currentArrayPath, value);
        var row = FindMappingRow(field);
        if (row == null && string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        row ??= EnsureMappingRow(field);
        row.SourcePath = normalized;
        ValidateMappingRowPath(row);
        _activeMappingKey = GetMappingKey(row);
        RemoveRowIfEmpty(row);
    }

    private void OnQuestionFieldDictCodeChanged(TargetFieldDescriptor field, string? value)
    {
        var row = FindMappingRow(field);
        if (row == null && string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        row ??= EnsureMappingRow(field);
        row.DictCode = value;
        _activeMappingKey = GetMappingKey(row);
        RemoveRowIfEmpty(row);
    }

    private void OnQuestionFieldDictMatchModeChanged(TargetFieldDescriptor field, string value)
    {
        var normalized = EsbFieldMapping.NormalizeDictMatchMode(value);
        var row = FindMappingRow(field);
        if (row == null && normalized == EsbFieldMapping.DefaultDictMatchMode)
        {
            return;
        }

        row ??= EnsureMappingRow(field);
        row.DictMatchMode = normalized;
        _activeMappingKey = GetMappingKey(row);
        RemoveRowIfEmpty(row);
    }

    private void OnQuestionFieldDefaultValueChanged(TargetFieldDescriptor field, string? value)
    {
        var row = FindMappingRow(field);
        if (row == null && string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        row ??= EnsureMappingRow(field);
        row.DefaultValue = value;
        _activeMappingKey = GetMappingKey(row);
        RemoveRowIfEmpty(row);
    }

    private void OnQuestionFieldValueExpressionChanged(TargetFieldDescriptor field, string? value)
    {
        var row = FindMappingRow(field);
        if (row == null && string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        row ??= EnsureMappingRow(field);
        row.ValueExpression = value;
        _activeMappingKey = GetMappingKey(row);
        RemoveRowIfEmpty(row);
    }

    private void OnQuestionFieldRequiredChanged(TargetFieldDescriptor field, bool value)
    {
        var row = FindMappingRow(field);
        if (row == null && !value)
        {
            return;
        }

        row ??= EnsureMappingRow(field);
        row.IsRequired = value;
        _activeMappingKey = GetMappingKey(row);
        RemoveRowIfEmpty(row);
    }

    private void OnQuestionFieldEnabledChanged(TargetFieldDescriptor field, bool value)
    {
        var row = FindMappingRow(field);
        if (row == null && value)
        {
            return;
        }

        row ??= EnsureMappingRow(field);
        row.IsEnabled = value;
        _activeMappingKey = GetMappingKey(row);
        RemoveRowIfEmpty(row);
    }

    private void OnQuestionFieldArrayPathChanged(TargetFieldDescriptor field, string? value)
    {
        if (field.MappingTarget != MappingTarget.SubCard || !field.CardId.HasValue)
        {
            return;
        }

        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        SetSubCardArrayPath(field.CardId.Value, normalized);

        var row = FindMappingRow(field);
        if (row != null)
        {
            row.ArrayPath = GetSubCardArrayPath(field.CardId.Value);
            _activeMappingKey = GetMappingKey(row);
            RemoveRowIfEmpty(row);
        }
    }

    private Task OnQuestionFieldFilterRulesChanged(TargetFieldDescriptor field, List<EsbFilterRule> rules)
    {
        var row = FindMappingRow(field);
        if (row == null && rules.Count == 0)
        {
            return Task.CompletedTask;
        }

        row ??= EnsureMappingRow(field);
        row.FilterRules = rules;
        _activeMappingKey = GetMappingKey(row);
        if (_pathPickTargetKind == PathPickTargetKind.FilterRule
            && string.Equals(_pathPickTargetKey, GetMappingKey(row), StringComparison.OrdinalIgnoreCase)
            && _pathPickRuleIndex >= rules.Count)
        {
            ClearPathPickState();
        }

        RemoveRowIfEmpty(row);
        return Task.CompletedTask;
    }

    private void ClearQuestionFieldSourcePath(TargetFieldDescriptor field)
    {
        var row = FindMappingRow(field);
        if (row == null)
        {
            return;
        }

        row.SourcePath = "";
        row.IsPathValid = true;
        _activeMappingKey = GetMappingKey(row);
        RemoveRowIfEmpty(row);
        if (FindMappingRow(field) != null)
        {
            ArmPathPick(field, advanceAfterPick: false);
        }
    }

    private void DeleteQuestionFieldMapping(TargetFieldDescriptor field)
    {
        var row = FindMappingRow(field);
        if (row == null)
        {
            return;
        }

        _mappingRows.Remove(row);
        if (string.Equals(_activeMappingKey, GetMappingKey(field), StringComparison.OrdinalIgnoreCase))
        {
            _activeMappingKey = FindCurrentTargetField(GetMappingKey(field)) == null ? null : GetMappingKey(field);
        }

        if (string.Equals(_pathPickTargetKey, GetMappingKey(field), StringComparison.OrdinalIgnoreCase))
        {
            ClearPathPickState();
        }
    }

    private void OpenQuestionFieldEditDialog(TargetFieldDescriptor field)
    {
        OpenQuestionFieldEditDialog(field, keepCurrentQuestionContext: false);
    }

    private void OpenQuestionFieldEditDialog(TargetFieldDescriptor field, bool keepCurrentQuestionContext)
    {
        if (!keepCurrentQuestionContext)
        {
            SelectQuestionField(field, armPathPick: false, advanceAfterPick: false);
        }
        else
        {
            ClearPathPickState();
        }

        var mappingKey = GetMappingKey(field);
        _editDialogCleanupMappingKey = FindMappingRow(field) == null ? mappingKey : null;

        var row = EnsureMappingRow(field);
        _activeMappingKey = GetMappingKey(row);
        var rowIndex = _mappingRows.FindIndex(r => GetMappingKey(r) == GetMappingKey(row));
        if (rowIndex >= 0)
        {
            OpenEditDialog(rowIndex);
        }
    }

    private void FocusQuestionField(TargetFieldDescriptor field) =>
        SelectQuestionField(field, armPathPick: false, advanceAfterPick: false);

    private void ArmQuestionFieldPathPick(TargetFieldDescriptor field)
    {
        _activeMappingKey = GetMappingKey(field);
        ArmPathPick(field, advanceAfterPick: false);
    }

    private bool IsQuestionFieldQuickEditActive(TargetFieldDescriptor field) =>
        IsQuestionTreeNodeSelected(GetQuestionTreeNodeKey(field))
        || IsPathPickActive(field)
        || string.Equals(_activeMappingKey, GetMappingKey(field), StringComparison.OrdinalIgnoreCase);

    private void ValidateMappingRowPath(WizardMappingRow row)
    {
        if (_parsedJson == null || string.IsNullOrWhiteSpace(row.SourcePath))
        {
            row.IsPathValid = true;
            return;
        }

        if (NeedsSubCardArrayPath(row))
        {
            row.IsPathValid = true;
            return;
        }

        row.IsPathValid = ResolvePreviewToken(row.MappingTarget, row.CardId, row.ArrayPath, row.SourcePath) != null;
    }

    private void OnActiveRowSourcePathChanged(string? value)
    {
        var field = SelectedStandaloneField;
        var row = ActiveRow;
        if (row == null && field == null)
        {
            return;
        }

        var mappingTarget = row?.MappingTarget ?? field!.MappingTarget;
        var cardId = row?.CardId ?? field!.CardId;
        var currentArrayPath = row?.ArrayPath
            ?? (field?.CardId.HasValue == true ? GetSubCardArrayPath(field.CardId.Value) : null);
        var normalized = NormalizeEditableSourcePath(mappingTarget, cardId, currentArrayPath, value);
        if (row == null)
        {
            if (string.IsNullOrWhiteSpace(normalized) || field == null)
            {
                return;
            }

            row = EnsureMappingRow(field);
        }

        row.SourcePath = normalized;
        ValidateMappingRowPath(row);
        _activeMappingKey = GetMappingKey(row);
        RemoveRowIfEmpty(row);
    }

    private void OnActiveRowDictCodeChanged(string? value)
    {
        var row = ActiveRow;
        if (row == null)
        {
            if (SelectedStandaloneField == null || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            row = EnsureMappingRow(SelectedStandaloneField);
        }

        row.DictCode = value;
        _activeMappingKey = GetMappingKey(row);
        RemoveRowIfEmpty(row);
    }

    private void OnActiveRowDictMatchModeChanged(string value)
    {
        var normalized = EsbFieldMapping.NormalizeDictMatchMode(value);
        var row = ActiveRow;
        if (row == null)
        {
            if (SelectedStandaloneField == null || normalized == EsbFieldMapping.DefaultDictMatchMode)
            {
                return;
            }

            row = EnsureMappingRow(SelectedStandaloneField);
        }

        row.DictMatchMode = normalized;
        _activeMappingKey = GetMappingKey(row);
        RemoveRowIfEmpty(row);
    }

    private void OnActiveRowDefaultValueChanged(string? value)
    {
        var row = ActiveRow;
        if (row == null)
        {
            if (SelectedStandaloneField == null || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            row = EnsureMappingRow(SelectedStandaloneField);
        }

        row.DefaultValue = value;
        _activeMappingKey = GetMappingKey(row);
        RemoveRowIfEmpty(row);
    }

    private void OnActiveRowValueExpressionChanged(string? value)
    {
        var row = ActiveRow;
        if (row == null)
        {
            if (SelectedStandaloneField == null || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            row = EnsureMappingRow(SelectedStandaloneField);
        }

        row.ValueExpression = value;
        _activeMappingKey = GetMappingKey(row);
        RemoveRowIfEmpty(row);
    }

    private void OnActiveRowRequiredChanged(bool value)
    {
        var row = ActiveRow;
        if (row == null)
        {
            if (SelectedStandaloneField == null || !value)
            {
                return;
            }

            row = EnsureMappingRow(SelectedStandaloneField);
        }

        row.IsRequired = value;
        _activeMappingKey = GetMappingKey(row);
        RemoveRowIfEmpty(row);
    }

    private void OnActiveRowEnabledChanged(bool value)
    {
        var row = ActiveRow;
        if (row == null)
        {
            if (SelectedStandaloneField == null || value)
            {
                return;
            }

            row = EnsureMappingRow(SelectedStandaloneField);
        }

        row.IsEnabled = value;
        _activeMappingKey = GetMappingKey(row);
        RemoveRowIfEmpty(row);
    }

    private void OnActiveRowArrayPathChanged(string? value)
    {
        if (ActiveRow != null)
        {
            ActiveRow.ArrayPath = value;
            SyncSubCardArrayPath(ActiveRow);
        }
    }

    private Task OnActiveRowFilterRulesChanged(List<EsbFilterRule> rules)
    {
        var row = ActiveRow;
        if (row == null)
        {
            if (SelectedStandaloneField == null || rules.Count == 0)
            {
                return Task.CompletedTask;
            }

            row = EnsureMappingRow(SelectedStandaloneField);
        }

        row.FilterRules = rules;
        _activeMappingKey = GetMappingKey(row);
        RemoveRowIfEmpty(row);
        return Task.CompletedTask;
    }

    private void ClearActiveRowSourcePath()
    {
        var row = ActiveRow;
        if (row == null)
        {
            return;
        }

        row.SourcePath = "";
        row.IsPathValid = true;
        RemoveRowIfEmpty(row);
        if (SelectedStandaloneField != null)
        {
            ArmPathPick(SelectedStandaloneField, advanceAfterPick: false);
        }
    }

    private void DeleteActiveRow()
    {
        var row = ActiveRow;
        if (row == null)
        {
            return;
        }

        var mappingKey = GetMappingKey(row);
        _mappingRows.Remove(row);
        _activeMappingKey = FindCurrentTargetField(mappingKey) == null ? null : mappingKey;
        ClearPathPickState();
    }

    private void OpenActiveRowEditDialog()
    {
        var row = ActiveRow;
        if (row == null && SelectedQuestionField != null)
        {
            OpenQuestionFieldEditDialog(SelectedQuestionField);
            return;
        }

        if (row == null)
        {
            return;
        }

        var rowIndex = _mappingRows.FindIndex(r => GetMappingKey(r) == GetMappingKey(row));
        if (rowIndex >= 0)
        {
            OpenEditDialog(rowIndex);
        }
    }

    private void OpenEditDialog(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _mappingRows.Count)
        {
            return;
        }

        var row = _mappingRows[rowIndex];
        _editDialogRowIndex = rowIndex;
        _editDialogItem = new EsbFieldMapping
        {
            TranCode = _selectedConfig?.TranCode ?? "",
            MappingTarget = row.MappingTarget,
            SourcePath = row.SourcePath,
            TargetField = row.TargetField,
            DictCode = row.DictCode,
            DictMatchMode = row.DictMatchMode,
            DefaultValue = row.DefaultValue,
            ValueExpression = row.ValueExpression,
            IsRequired = row.IsRequired,
            IsEnabled = row.IsEnabled,
            CardId = row.CardId,
            ArrayPath = row.MappingTarget == MappingTarget.SubCard && row.CardId.HasValue
                ? row.ArrayPath ?? GetSubCardArrayPath(row.CardId.Value)
                : row.ArrayPath,
        };
        _editDialogFilterRules = row.FilterRules.Select(r => new EsbFilterRule
        {
            SourcePath = r.SourcePath,
            RuleGroup = r.RuleGroup,
            Operator = r.Operator,
            CompareValue = r.CompareValue,
            FilterScope = r.FilterScope,
            IsEnabled = r.IsEnabled,
        }).ToList();

        _editDialogVisible = true;
    }

    private void OnEditDialogSave()
    {
        if (_editDialogItem == null || _editDialogRowIndex < 0 || _editDialogRowIndex >= _mappingRows.Count)
        {
            return;
        }

        var row = _mappingRows[_editDialogRowIndex];
        row.DictCode = _editDialogItem.DictCode;
        row.DictMatchMode = EsbFieldMapping.NormalizeDictMatchMode(_editDialogItem.DictMatchMode);
        row.DefaultValue = _editDialogItem.DefaultValue;
        row.ValueExpression = _editDialogItem.ValueExpression;
        row.IsRequired = _editDialogItem.IsRequired;
        row.IsEnabled = _editDialogItem.IsEnabled;
        row.CardId = _editDialogItem.CardId;
        row.ArrayPath = NormalizeEditableSubCardArrayPathValue(_editDialogItem.ArrayPath);
        row.SourcePath = NormalizeEditableSourcePath(row.MappingTarget, row.CardId, row.ArrayPath, _editDialogItem.SourcePath);
        row.FilterRules = _editDialogFilterRules.ToList();
        SyncSubCardArrayPath(row);
        ValidateMappingRowPath(row);
        RemoveRowIfEmpty(row);

        _editDialogCleanupMappingKey = null;
        _editDialogVisible = false;
        _activeMappingKey = _mappingRows.Contains(row) ? GetMappingKey(row) : null;
        inj_snackbar.Add("映射配置已更新", Severity.Success);
    }

    private void OnEditDialogVisibleChanged(bool value)
    {
        _editDialogVisible = value;
        if (value)
        {
            return;
        }

        CleanupEditDialogTempRow();
    }

    private void CleanupEditDialogTempRow()
    {
        if (string.IsNullOrWhiteSpace(_editDialogCleanupMappingKey))
        {
            return;
        }

        var row = _mappingRows.FirstOrDefault(r =>
            string.Equals(GetMappingKey(r), _editDialogCleanupMappingKey, StringComparison.OrdinalIgnoreCase));

        _editDialogCleanupMappingKey = null;

        if (row != null)
        {
            RemoveRowIfEmpty(row);
        }
    }

    private string? GetExistingArrayPath(Guid cardId) =>
        _subCardArrayPathOverrides.TryGetValue(cardId, out var overrideValue)
            ? NormalizeEditableSubCardArrayPathValue(overrideValue)
            : GetAllSubCardArrayPathValues(cardId).FirstOrDefault();

    private List<string> GetAllSubCardArrayPathValues(Guid cardId) =>
        _subCardArrayPathOverrides.TryGetValue(cardId, out var overrideValue)
            ? (string.IsNullOrWhiteSpace(NormalizeEditableSubCardArrayPathValue(overrideValue))
                ? new List<string>()
                : new List<string> { NormalizeEditableSubCardArrayPathValue(overrideValue)! })
            : _mappingRows
                .Where(r => r.MappingTarget == MappingTarget.SubCard
                    && r.CardId == cardId
                    && !string.IsNullOrWhiteSpace(r.ArrayPath))
                .Select(r => NormalizeEditableSubCardArrayPathValue(r.ArrayPath))
                .Concat(_existingMappings
                    .Where(m => m.MappingTarget == MappingTarget.SubCard
                        && m.CardId == cardId
                        && !string.IsNullOrWhiteSpace(m.ArrayPath))
                    .Select(m => NormalizeEditableSubCardArrayPathValue(m.ArrayPath)))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    private bool HasMixedSubCardArrayPaths(Guid cardId) => GetAllSubCardArrayPathValues(cardId).Count > 1;

    private string GetSubCardArrayPath(Guid cardId) => GetExistingArrayPath(cardId) ?? "";

    private void SetSubCardArrayPath(Guid cardId, string? value)
    {
        var normalized = NormalizeEditableSubCardArrayPathValue(value);
        _subCardArrayPathOverrides[cardId] = normalized;

        foreach (var row in _mappingRows.Where(r => r.MappingTarget == MappingTarget.SubCard && r.CardId == cardId))
        {
            row.ArrayPath = normalized;
            ValidateMappingRowPath(row);
        }
    }

    private void SyncSubCardArrayPath(WizardMappingRow row)
    {
        if (row.MappingTarget == MappingTarget.SubCard && row.CardId.HasValue)
        {
            SetSubCardArrayPath(row.CardId.Value, row.ArrayPath);
            row.ArrayPath = GetSubCardArrayPath(row.CardId.Value);
        }
    }

    private string ResolveSuggestionArrayPath(
        LlmSuggestionItem suggestion,
        IReadOnlyDictionary<Guid, string?>? groupArrayPaths = null)
    {
        if (suggestion.MappingTarget != MappingTarget.SubCard || !suggestion.CardId.HasValue)
        {
            return suggestion.ArrayPath ?? "";
        }

        if (groupArrayPaths?.TryGetValue(suggestion.CardId.Value, out var groupArrayPath) == true
            && !string.IsNullOrWhiteSpace(groupArrayPath))
        {
            SetSubCardArrayPath(suggestion.CardId.Value, groupArrayPath);
            return GetSubCardArrayPath(suggestion.CardId.Value);
        }

        var editableArrayPath = ResolveSuggestionDisplayArrayPath(suggestion);
        if (!string.IsNullOrWhiteSpace(editableArrayPath))
        {
            SetSubCardArrayPath(suggestion.CardId.Value, editableArrayPath);
            return GetSubCardArrayPath(suggestion.CardId.Value);
        }

        return GetSubCardArrayPath(suggestion.CardId.Value);
    }

    private string GetSubCardArrayPathStatusText(Guid cardId)
    {
        if (HasMixedSubCardArrayPaths(cardId))
        {
            return "容器路径不一致";
        }

        if (string.IsNullOrWhiteSpace(GetSubCardArrayPath(cardId)))
        {
            return "容器路径未配置";
        }

        return HasInvalidSubCardArrayPath(cardId)
            ? GetSubCardArrayPathIssueText(cardId)
            : "容器路径已配置";
    }

    private Color GetSubCardArrayPathStatusColor(Guid cardId)
    {
        if (HasMixedSubCardArrayPaths(cardId))
        {
            return Color.Error;
        }

        if (string.IsNullOrWhiteSpace(GetSubCardArrayPath(cardId)))
        {
            return Color.Warning;
        }

        return HasInvalidSubCardArrayPath(cardId)
            ? Color.Error
            : Color.Success;
    }

    private void OnSelectedSubCardArrayPathChanged(string? value)
    {
        if (CurrentSubCardGroupCard != null)
        {
            SetSubCardArrayPath(CurrentSubCardGroupCard.CardId, value);
        }
    }

    private List<EsbFieldMapping> BuildPreviewMappings() => BuildMappings(includeDisabled: false);

    private List<EsbFieldMapping> BuildMappingsForSave() => BuildMappings(includeDisabled: true);

    private static EsbFilterRule CloneFilterRule(EsbFilterRule rule) => new()
    {
        SourcePath = rule.SourcePath,
        RuleGroup = rule.RuleGroup,
        Operator = rule.Operator,
        CompareValue = rule.CompareValue,
        FilterScope = rule.FilterScope,
        IsEnabled = rule.IsEnabled,
        Description = rule.Description,
        SortOrder = rule.SortOrder,
    };

    private static List<EsbFilterRule> CloneFilterRules(IEnumerable<EsbFilterRule> rules) =>
        rules.Select(CloneFilterRule).ToList();

    private string? GetPendingSubCardArrayPath(WizardMappingRow row)
    {
        var arrayPath = GetEffectiveArrayPath(row.CardId, row.ArrayPath, row.SourcePath);
        if (!string.IsNullOrWhiteSpace(arrayPath))
        {
            return arrayPath;
        }

        foreach (var rule in row.FilterRules)
        {
            if (TryInferSubCardArrayPathFromSourcePath(rule.SourcePath, out var ruleArrayPath))
            {
                return ruleArrayPath;
            }
        }

        return null;
    }

    private bool NeedsSubCardPathSaveNormalization(string? sourcePath, string? arrayPath)
    {
        var normalizedSourcePath = sourcePath?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(normalizedSourcePath) || !SubCardPathHelper.HasArrayWildcard(normalizedSourcePath))
        {
            return false;
        }

        var normalizedArrayPath = NormalizeSubCardArrayPathValue(arrayPath);
        return (!string.IsNullOrWhiteSpace(normalizedArrayPath)
                && SubCardPathHelper.TryBuildRelativePath(normalizedSourcePath, normalizedArrayPath, out _))
            || TryInferSubCardArrayPathFromSourcePath(normalizedSourcePath, out _);
    }

    private int CountPendingSubCardSaveNormalization() =>
        _mappingRows
            .Where(r => r.MappingTarget == MappingTarget.SubCard && HasValueSource(r))
            .Sum(r =>
                (NeedsSubCardPathSaveNormalization(r.SourcePath, r.ArrayPath) ? 1 : 0)
                + r.FilterRules.Count(rule => NeedsSubCardPathSaveNormalization(rule.SourcePath, r.ArrayPath)))
        + _subCardFilterRulesByCardId.Sum(pair =>
            pair.Value.Count(rule => NeedsSubCardPathSaveNormalization(rule.SourcePath, GetSubCardArrayPath(pair.Key))));

    private async Task<bool> ConfirmSubCardPathNormalizationAsync()
    {
        var pendingCount = CountPendingSubCardSaveNormalization();
        if (pendingCount == 0)
        {
            return true;
        }

        var confirmed = await inj_dialogService.ShowMessageBox(
            "保存前确认",
            $"检测到 {pendingCount} 处子卡完整路径，保存时会自动转换为数组项内相对路径，并保留 ArrayPath。是否继续保存？",
            yesText: "继续保存",
            cancelText: "取消");
        return confirmed == true;
    }

    private bool TryNormalizeSubCardPathForSave(
        string? sourcePath,
        string? arrayPath,
        string pathLabel,
        out string normalizedSourcePath,
        out string? normalizedArrayPath,
        out string? error)
    {
        normalizedSourcePath = sourcePath?.Trim() ?? "";
        normalizedArrayPath = NormalizeSubCardArrayPathValue(arrayPath);
        error = null;

        if (string.IsNullOrWhiteSpace(normalizedSourcePath))
        {
            return true;
        }

        var effectiveArrayPath = ExpandSubCardArrayPathToRoot(normalizedArrayPath);
        if (!string.IsNullOrWhiteSpace(effectiveArrayPath)
            && SubCardPathHelper.TryBuildRelativePath(normalizedSourcePath, effectiveArrayPath, out var currentRelativePath))
        {
            if (string.IsNullOrWhiteSpace(currentRelativePath))
            {
                error = $"{pathLabel} 必须选到数组项内的具体字段，不能直接停留在 []。";
                return false;
            }

            normalizedSourcePath = currentRelativePath;
            return true;
        }

        if (TryNormalizeMainRecordSourcePath(normalizedSourcePath, out var mainRecordScopedPath)
            && !SubCardPathHelper.HasArrayWildcard(TrimMainRecordScopePrefix(mainRecordScopedPath)))
        {
            normalizedSourcePath = mainRecordScopedPath;
            return true;
        }

        if (TryInferSubCardContainerPath(
                normalizedSourcePath,
                out var pickedArrayPath,
                out var relativePath,
                string.IsNullOrWhiteSpace(normalizedArrayPath)))
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                error = $"{pathLabel} 必须选到数组项内的具体字段，不能直接停留在 []。";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(normalizedArrayPath)
                && !SubCardPathHelper.PathsEqual(normalizedArrayPath, pickedArrayPath))
            {
                error = $"{pathLabel} 与当前 ArrayPath 不一致，请先统一后再保存。";
                return false;
            }

            normalizedArrayPath = pickedArrayPath;
            normalizedSourcePath = relativePath;
            return true;
        }

        if (!SubCardPathHelper.IsAbsoluteJsonPath(normalizedSourcePath)
            && !IsMainRecordScopedPath(normalizedSourcePath)
            && !UsesRelativeSubCardPath(MappingTarget.SubCard, null, normalizedArrayPath, normalizedSourcePath))
        {
            normalizedSourcePath = SubCardPathHelper.EnsureAbsoluteJsonPath(normalizedSourcePath);
        }

        return true;
    }

    private bool TryBuildNormalizedSavePayload(
        out List<EsbFieldMapping> finalMappings,
        out Dictionary<string, List<EsbFilterRule>> normalizedRuleMap,
        out string? error)
    {
        finalMappings = BuildMappingsForSave();
        normalizedRuleMap = new Dictionary<string, List<EsbFilterRule>>(StringComparer.OrdinalIgnoreCase);
        error = null;

        foreach (var mapping in finalMappings)
        {
            var mappingKey = GetMappingKey(mapping.MappingTarget, mapping.TargetField, mapping.CardId);
            var row = _mappingRows.FirstOrDefault(r =>
                string.Equals(GetMappingKey(r), mappingKey, StringComparison.OrdinalIgnoreCase));
            var rowLabel = row?.TargetFieldDisplayName ?? mapping.TargetField;

            if (mapping.MappingTarget == MappingTarget.SubCard)
            {
                if (!TryNormalizeSubCardPathForSave(
                        mapping.SourcePath,
                        mapping.ArrayPath,
                        $"子卡字段【{rowLabel}】的源路径",
                        out var normalizedSourcePath,
                        out var normalizedArrayPath,
                        out error))
                {
                    return false;
                }

                mapping.SourcePath = normalizedSourcePath;
                mapping.ArrayPath = normalizedArrayPath;

                var normalizedRules = new List<EsbFilterRule>();
                foreach (var rule in row?.FilterRules ?? [])
                {
                    var clonedRule = CloneFilterRule(rule);
                    if (!TryNormalizeSubCardPathForSave(
                            clonedRule.SourcePath,
                            mapping.ArrayPath,
                            $"子卡字段【{rowLabel}】的过滤路径",
                            out var normalizedRulePath,
                            out var normalizedRuleArrayPath,
                            out error))
                    {
                        return false;
                    }

                    if (!string.IsNullOrWhiteSpace(normalizedRuleArrayPath)
                        && string.IsNullOrWhiteSpace(mapping.ArrayPath))
                    {
                        mapping.ArrayPath = normalizedRuleArrayPath;
                    }

                    if (!string.IsNullOrWhiteSpace(normalizedRuleArrayPath)
                        && !string.IsNullOrWhiteSpace(mapping.ArrayPath)
                        && !SubCardPathHelper.PathsEqual(normalizedRuleArrayPath, mapping.ArrayPath))
                    {
                        error = $"子卡字段【{rowLabel}】的过滤路径与当前 ArrayPath 不一致，请先统一后再保存。";
                        return false;
                    }

                    clonedRule.SourcePath = normalizedRulePath;
                    normalizedRules.Add(clonedRule);
                }

                normalizedRuleMap[mappingKey] = normalizedRules;
                continue;
            }

            normalizedRuleMap[mappingKey] = CloneFilterRules(row?.FilterRules ?? []);
        }

        foreach (var pair in _subCardFilterRulesByCardId.Where(pair => pair.Value.Count > 0))
        {
            var cardId = pair.Key;
            var normalizedArrayPath = NormalizeSubCardArrayPathValue(GetSubCardArrayPath(cardId));
            var normalizedRules = new List<EsbFilterRule>();

            foreach (var rule in pair.Value)
            {
                var clonedRule = CloneFilterRule(rule);
                if (!TryNormalizeSubCardPathForSave(
                        clonedRule.SourcePath,
                        normalizedArrayPath,
                        "SubCard级过滤路径",
                        out var normalizedRulePath,
                        out var normalizedRuleArrayPath,
                        out error))
                {
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(normalizedRuleArrayPath)
                    && string.IsNullOrWhiteSpace(normalizedArrayPath))
                {
                    normalizedArrayPath = normalizedRuleArrayPath;
                }

                if (!string.IsNullOrWhiteSpace(normalizedRuleArrayPath)
                    && !string.IsNullOrWhiteSpace(normalizedArrayPath)
                    && !SubCardPathHelper.PathsEqual(normalizedRuleArrayPath, normalizedArrayPath))
                {
                    error = "SubCard级过滤路径与当前 ArrayPath 不一致，请先统一后再保存。";
                    return false;
                }

                clonedRule.SourcePath = normalizedRulePath;
                normalizedRules.Add(clonedRule);
            }

            finalMappings.Add(new EsbFieldMapping
            {
                TranCode = _selectedConfig!.TranCode,
                MappingTarget = MappingTarget.SubCard,
                SourcePath = "",
                TargetField = EsbFieldMapping.SubCardFilterTargetField,
                CardId = cardId,
                ArrayPath = normalizedArrayPath,
                IsEnabled = true,
                SortOrder = finalMappings.Count + 1,
            });

            normalizedRuleMap[GetMappingKey(MappingTarget.SubCard, EsbFieldMapping.SubCardFilterTargetField, cardId)] = normalizedRules;
        }

        return true;
    }

    private List<EsbFieldMapping> BuildMappings(bool includeDisabled)
    {
        var result = new List<EsbFieldMapping>();
        var sortOrder = 0;

        foreach (var cardId in _mappingRows
                     .Where(r => r.MappingTarget == MappingTarget.SubCard && r.CardId.HasValue)
                     .Select(r => r.CardId!.Value)
                     .Distinct())
        {
            SetSubCardArrayPath(cardId, GetSubCardArrayPath(cardId));
        }

        foreach (var row in _mappingRows.Where(r =>
                     HasValueSource(r)
                     && (includeDisabled || r.IsEnabled)))
        {
            sortOrder++;
            result.Add(new EsbFieldMapping
            {
                TranCode = _selectedConfig!.TranCode,
                MappingTarget = row.MappingTarget,
                SourcePath = row.SourcePath,
                TargetField = row.TargetField,
                CardId = row.MappingTarget == MappingTarget.SubCard ? row.CardId : null,
                ArrayPath = row.MappingTarget == MappingTarget.SubCard ? NormalizeSubCardArrayPathValue(row.ArrayPath) : null,
                DictCode = row.DictCode,
                DictMatchMode = EsbFieldMapping.NormalizeDictMatchMode(row.DictMatchMode),
                DefaultValue = row.DefaultValue,
                ValueExpression = row.ValueExpression,
                IsRequired = row.IsRequired,
                IsEnabled = row.IsEnabled,
                SortOrder = sortOrder,
            });
        }

        return result;
    }

    private async Task SaveAll()
    {
        if (_selectedConfig == null)
        {
            inj_snackbar.Add("请先选择接口。", Severity.Warning);
            return;
        }

        if (_parsedJson == null)
        {
            inj_snackbar.Add("当前 JSON 无效，无法保存。", Severity.Warning);
            return;
        }

        var incompleteRows = _mappingRows.Where(r => r.IsEnabled && !HasValueSource(r)).ToList();
        if (incompleteRows.Count > 0)
        {
            inj_snackbar.Add($"仍有 {incompleteRows.Count} 条映射既没有源路径也没有默认值，请先补全。", Severity.Warning);
            return;
        }

        var incompleteSubCards = _mappingRows
            .Where(HasIncompleteSubCardContext)
            .ToList();
        if (incompleteSubCards.Count > 0)
        {
            inj_snackbar.Add("子卡映射缺少 CardId 或 ArrayPath，请先补全。", Severity.Warning);
            return;
        }

        if (!await ConfirmSubCardPathNormalizationAsync())
        {
            return;
        }

        if (!TryBuildNormalizedSavePayload(out var finalMappings, out var normalizedRuleMap, out var saveError))
        {
            inj_snackbar.Add(saveError ?? "子卡路径归一化失败，请检查配置。", Severity.Warning);
            return;
        }

        if (finalMappings.Count == 0 && _filterRules.Count == 0 && !HasPersistedWorkbenchContent)
        {
            inj_snackbar.Add("没有可保存的内容。", Severity.Warning);
            return;
        }

        _saving = true;

        try
        {
            await using var db = await ContextFactory.CreateDbContextAsync();
            var tranCode = _selectedConfig.TranCode;
            var integrationProjectCode = _selectedConfig.IntegrationProjectCode;

            if (_isRebuildMode)
            {
                var oldMappings = await db.EsbFieldMappings
                    .Where(m => m.TranCode == tranCode && m.IntegrationProjectCode == integrationProjectCode)
                    .ToListAsync();
                if (oldMappings.Count > 0)
                {
                    var oldIds = oldMappings.Select(m => m.Id).ToList();
                    var oldRules = await db.EsbFilterRules
                        .Where(r => r.MappingId != null && oldIds.Contains(r.MappingId.Value))
                        .ToListAsync();
                    db.EsbFilterRules.RemoveRange(oldRules);
                    db.EsbFieldMappings.RemoveRange(oldMappings);
                }
            }
            else
            {
                var keptExistingIds = _mappingRows
                    .Where(r => r.Origin == "existing" && r.ExistingId > 0)
                    .Select(r => r.ExistingId)
                    .ToHashSet();

                var removedMappings = await db.EsbFieldMappings
                    .Where(m => m.TranCode == tranCode && m.IntegrationProjectCode == integrationProjectCode && !keptExistingIds.Contains(m.Id))
                    .ToListAsync();

                if (removedMappings.Count > 0)
                {
                    var removedIds = removedMappings.Select(m => m.Id).ToList();
                    var removedRules = await db.EsbFilterRules
                        .Where(r => r.MappingId != null && removedIds.Contains(r.MappingId.Value))
                        .ToListAsync();
                    db.EsbFilterRules.RemoveRange(removedRules);
                    db.EsbFieldMappings.RemoveRange(removedMappings);
                }
            }

            foreach (var mapping in finalMappings)
            {
                var existingRow = !_isRebuildMode
                    ? _mappingRows.FirstOrDefault(r =>
                        r.Origin == "existing"
                        && r.ExistingId > 0
                        && string.Equals(
                            GetMappingKey(r),
                            GetMappingKey(mapping.MappingTarget, mapping.TargetField, mapping.CardId),
                            StringComparison.OrdinalIgnoreCase))
                    : null;

                if (existingRow != null)
                {
                    var entity = await db.EsbFieldMappings.FindAsync(existingRow.ExistingId);
                    if (entity != null)
                    {
                        entity.SourcePath = mapping.SourcePath;
                        entity.DictCode = mapping.DictCode;
                        entity.DictMatchMode = EsbFieldMapping.NormalizeDictMatchMode(mapping.DictMatchMode);
                        entity.DefaultValue = mapping.DefaultValue;
                        entity.ValueExpression = mapping.ValueExpression;
                        entity.IsRequired = mapping.IsRequired;
                        entity.IsEnabled = mapping.IsEnabled;
                        entity.SortOrder = mapping.SortOrder;
                        entity.CardId = mapping.CardId;
                        entity.ArrayPath = mapping.ArrayPath;
                        entity.IntegrationProjectCode = integrationProjectCode;

                        var oldRules = await db.EsbFilterRules
                            .Where(r => r.MappingId == existingRow.ExistingId)
                            .ToListAsync();
                        db.EsbFilterRules.RemoveRange(oldRules);

                        var mappingKey = GetMappingKey(mapping.MappingTarget, mapping.TargetField, mapping.CardId);
                        foreach (var rule in normalizedRuleMap.GetValueOrDefault(mappingKey) ?? [])
                        {
                            rule.Id = 0;
                            rule.TranCode = tranCode;
                            rule.MappingId = existingRow.ExistingId;
                            rule.RuleGroup = Math.Max(1, rule.RuleGroup);
                            rule.IntegrationProjectCode = integrationProjectCode;
                            db.EsbFilterRules.Add(rule);
                        }
                    }
                }
                else
                {
                    mapping.Id = 0;
                    mapping.IntegrationProjectCode = integrationProjectCode;
                    db.EsbFieldMappings.Add(mapping);
                    await db.SaveChangesAsync();

                    var mappingKey = GetMappingKey(mapping.MappingTarget, mapping.TargetField, mapping.CardId);
                    if (normalizedRuleMap.TryGetValue(mappingKey, out var normalizedRules) && normalizedRules.Count > 0)
                    {
                        foreach (var rule in normalizedRules)
                        {
                            rule.Id = 0;
                            rule.TranCode = tranCode;
                            rule.MappingId = mapping.Id;
                            rule.RuleGroup = Math.Max(1, rule.RuleGroup);
                            rule.IntegrationProjectCode = integrationProjectCode;
                            db.EsbFilterRules.Add(rule);
                        }
                    }
                }
            }

            var oldInterfaceRules = await db.EsbFilterRules
                .Where(r => r.TranCode == tranCode && r.MappingId == null && r.IntegrationProjectCode == integrationProjectCode)
                .ToListAsync();
            db.EsbFilterRules.RemoveRange(oldInterfaceRules);

            for (var i = 0; i < _filterRules.Count; i++)
            {
                var rule = _filterRules[i];
                rule.Id = 0;
                rule.TranCode = tranCode;
                rule.SortOrder = i + 1;
                rule.RuleGroup = Math.Max(1, rule.RuleGroup);
                rule.IntegrationProjectCode = integrationProjectCode;
                db.EsbFilterRules.Add(rule);
            }

            await db.SaveChangesAsync();

            var savedMappings = await db.EsbFieldMappings
                .Where(m => m.TranCode == tranCode && m.IntegrationProjectCode == integrationProjectCode)
                .OrderBy(m => m.SortOrder)
                .ToListAsync();

            _existingMappings = savedMappings
                .Where(m => !EsbFieldMapping.IsSubCardFilterMapping(m))
                .ToList();

            _existingMappingCount = _existingMappings.Count;
            _existingInterfaceRuleCount = _filterRules.Count;
            _existingSubCardFilterRuleCount = _subCardFilterRulesByCardId.Values.Sum(rules => rules.Count);
            var idMap = _existingMappings.ToDictionary(
                m => GetMappingKey(m.MappingTarget, m.TargetField, m.CardId),
                m => m.Id,
                StringComparer.OrdinalIgnoreCase);

            foreach (var row in _mappingRows.Where(HasValueSource))
            {
                if (row.MappingTarget == MappingTarget.SubCard)
                {
                    var inferredArrayPath = GetPendingSubCardArrayPath(row);
                    if (!string.IsNullOrWhiteSpace(inferredArrayPath))
                    {
                        row.ArrayPath = inferredArrayPath;
                        SyncSubCardArrayPath(row);
                    }
                }

                row.Origin = "existing";
                if (idMap.TryGetValue(GetMappingKey(row), out var id))
                {
                    row.ExistingId = id;
                }
            }

            ConfigSvc.ClearCache();
            inj_snackbar.Add("保存成功。", Severity.Success);
        }
        catch (Exception ex)
        {
            inj_snackbar.Add($"保存失败: {ex.Message}", Severity.Error);
        }
        finally
        {
            _saving = false;
        }
    }

    private static Color GetTargetColor(MappingTarget target) => target switch
    {
        MappingTarget.Patient => Color.Primary,
        MappingTarget.Event => Color.Info,
        MappingTarget.Question => Color.Success,
        MappingTarget.SubCard => Color.Warning,
        _ => Color.Default,
    };

}
