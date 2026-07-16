using DataSync.LHYY.V2.Data;
using DataSync.LHYY.V2.Models.Dto;
using DataSync.LHYY.V2.Models.Entities;
using DataSync.LHYY.V2.Models.Enums;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataSync.LHYY.V2.Services;

public sealed class ConfigSyncService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IDbContextFactory<DataSyncDbContext> _contextFactory;
    private readonly ConfigService _configService;
    private readonly BioCoreIntegrationService _bioCoreService;

    public ConfigSyncService(
        IDbContextFactory<DataSyncDbContext> contextFactory,
        ConfigService configService,
        BioCoreIntegrationService bioCoreService)
    {
        _contextFactory = contextFactory;
        _configService = configService;
        _bioCoreService = bioCoreService;
    }

    public string SerializePackage(ConfigSyncPackage package) =>
        JsonSerializer.Serialize(package, JsonOptions);

    public ConfigSyncPackage DeserializePackage(string json)
    {
        var package = JsonSerializer.Deserialize<ConfigSyncPackage>(json, JsonOptions)
            ?? throw new InvalidOperationException("同步包内容为空或格式不正确。");

        if (!string.Equals(package.PackageType, ConfigSyncConstants.PackageType, StringComparison.Ordinal))
            throw new InvalidOperationException("同步包类型不匹配。");

        if (package.Version != 1)
            throw new InvalidOperationException($"不支持的同步包版本：{package.Version}。");

        NormalizePackage(package);
        return package;
    }

    public async Task<ConfigSyncPackage> ExportAsync(
        string projectCode,
        ConfigSyncExportOptions options,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var package = new ConfigSyncPackage
        {
            SourceProjectCode = projectCode,
            ExportedAt = DateTime.Now
        };

        if (options.IncludeMappings)
        {
            var nameCatalog = await BuildTargetNameCatalogAsync(projectCode, null, cancellationToken);
            var mappings = await db.EsbFieldMappings
                .Where(m => m.IntegrationProjectCode == projectCode)
                .OrderBy(m => m.TranCode)
                .ThenBy(m => m.MappingTarget)
                .ThenBy(m => m.CardId)
                .ThenBy(m => m.TargetField)
                .ThenBy(m => m.SortOrder)
                .ToListAsync(cancellationToken);

            var changed = false;
            foreach (var mapping in mappings.Where(m => string.IsNullOrWhiteSpace(m.SyncKey)))
            {
                mapping.SyncKey = Guid.NewGuid().ToString("N");
                changed = true;
            }

            if (changed)
                await db.SaveChangesAsync(cancellationToken);
            changed = false;

            var mappingIds = mappings.Select(m => m.Id).ToList();
            var mappingRules = mappingIds.Count == 0
                ? new List<EsbFilterRule>()
                : await db.EsbFilterRules
                    .Where(r => r.MappingId != null && mappingIds.Contains(r.MappingId.Value))
                    .OrderBy(r => r.MappingId)
                    .ThenBy(r => r.RuleGroup)
                    .ThenBy(r => r.SortOrder)
                    .ToListAsync(cancellationToken);
            var ruleMap = mappingRules
                .GroupBy(r => r.MappingId!.Value)
                .ToDictionary(g => g.Key, g => g.Select(ToPackageRule).ToList());

            var packageMappings = new List<ConfigSyncFieldMapping>();
            foreach (var mapping in mappings)
            {
                var item = ToPackageMapping(mapping, ruleMap.GetValueOrDefault(mapping.Id) ?? [], nameCatalog);
                item.ContentHash = ComputeMappingHash(item);

                if (!string.Equals(mapping.LastSyncHash, item.ContentHash, StringComparison.Ordinal))
                {
                    mapping.LastSyncHash = item.ContentHash;
                    changed = true;
                }

                packageMappings.Add(item);
            }

            if (changed)
                await db.SaveChangesAsync(cancellationToken);

            package.FieldMappings = packageMappings;
        }

        if (options.IncludeInterfaces)
        {
            var interfaces = await db.EsbInterfaceConfigs
                .Where(c => c.IntegrationProjectCode == projectCode)
                .OrderBy(c => c.SortOrder)
                .ThenBy(c => c.TranCode)
                .ToListAsync(cancellationToken);
            var tranCodes = interfaces.Select(i => i.TranCode).ToList();

            var filterRules = tranCodes.Count == 0
                ? new List<EsbFilterRule>()
                : await db.EsbFilterRules
                    .Where(r => r.IntegrationProjectCode == projectCode
                                && r.MappingId == null
                                && tranCodes.Contains(r.TranCode))
                    .OrderBy(r => r.TranCode)
                    .ThenBy(r => r.RuleGroup)
                    .ThenBy(r => r.SortOrder)
                    .ToListAsync(cancellationToken);
            var filterMap = filterRules
                .GroupBy(r => r.TranCode)
                .ToDictionary(g => g.Key, g => g.Select(ToPackageRule).ToList(), StringComparer.OrdinalIgnoreCase);

            var matchRules = tranCodes.Count == 0
                ? new List<EsbInterfaceMatchRule>()
                : await db.EsbInterfaceMatchRules
                    .Where(r => r.IntegrationProjectCode == projectCode && tranCodes.Contains(r.TranCode))
                    .OrderBy(r => r.TranCode)
                    .ThenBy(r => r.MatchGroup)
                    .ThenBy(r => r.SortOrder)
                    .ToListAsync(cancellationToken);
            var matchMap = matchRules
                .GroupBy(r => r.TranCode)
                .ToDictionary(g => g.Key, g => g.Select(ToPackageMatchRule).ToList(), StringComparer.OrdinalIgnoreCase);

            var idempotentParts = tranCodes.Count == 0
                ? new List<EsbIdempotentKeyPart>()
                : await db.EsbIdempotentKeyParts
                    .Where(p => p.IntegrationProjectCode == projectCode && tranCodes.Contains(p.TranCode))
                    .OrderBy(p => p.TranCode)
                    .ThenBy(p => p.SortOrder)
                    .ToListAsync(cancellationToken);
            var idempotentMap = idempotentParts
                .GroupBy(p => p.TranCode)
                .ToDictionary(g => g.Key, g => g.Select(ToPackageIdempotentPart).ToList(), StringComparer.OrdinalIgnoreCase);

            package.Interfaces = interfaces.Select(config =>
            {
                var item = ToPackageInterface(
                    config,
                    filterMap.GetValueOrDefault(config.TranCode) ?? [],
                    matchMap.GetValueOrDefault(config.TranCode) ?? [],
                    idempotentMap.GetValueOrDefault(config.TranCode) ?? []);
                item.ContentHash = ComputeInterfaceHash(item);
                return item;
            }).ToList();
        }

        if (options.IncludeDictionaries)
        {
            var dicts = await db.EsbDicts
                .Where(d => d.IntegrationProjectCode == projectCode)
                .OrderBy(d => d.DictCode)
                .ThenBy(d => d.SortOrder)
                .ThenBy(d => d.SourceValue)
                .ToListAsync(cancellationToken);

            package.DictEntries = dicts.Select(dict =>
            {
                var item = ToPackageDict(dict);
                item.ContentHash = ComputeDictHash(item);
                return item;
            }).ToList();
        }

        if (options.IncludeProjectConfigs)
        {
            var configs = await db.EsbIntegrationProjectConfigs
                .Where(c => c.IntegrationProjectCode == projectCode)
                .OrderBy(c => c.ConfigKey)
                .ToListAsync(cancellationToken);

            package.ProjectConfigs = configs.Select(config =>
            {
                var item = ToPackageProjectConfig(config);
                item.ContentHash = ComputeProjectConfigHash(item);
                return item;
            }).ToList();
        }

        return package;
    }

    public async Task<ConfigSyncPreviewResult> PreviewAsync(
        ConfigSyncPackage package,
        string targetProjectCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var result = new ConfigSyncPreviewResult
        {
            PackageId = package.PackageId,
            SourceProjectCode = package.SourceProjectCode,
            ExportedAt = package.ExportedAt,
            TargetProjectCode = targetProjectCode
        };
        var nameCatalog = await BuildTargetNameCatalogAsync(targetProjectCode, package, cancellationToken);

        await AppendInterfacePreviewAsync(db, package, targetProjectCode, result, cancellationToken);
        await AppendMappingPreviewAsync(db, package, targetProjectCode, result, nameCatalog, cancellationToken);
        await AppendDictPreviewAsync(db, package, targetProjectCode, result, cancellationToken);
        await AppendProjectConfigPreviewAsync(db, package, targetProjectCode, result, cancellationToken);

        result.Items = result.Items
            .OrderBy(i => i.ItemType)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return result;
    }

    public async Task<ConfigSyncApplyResult> ApplyAsync(
        ConfigSyncPackage package,
        string targetProjectCode,
        IReadOnlyCollection<string> selectedItemKeys,
        CancellationToken cancellationToken = default)
    {
        var selected = selectedItemKeys.ToHashSet(StringComparer.Ordinal);
        var result = new ConfigSyncApplyResult();

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await ApplyInterfacesAsync(db, package, targetProjectCode, selected, result, cancellationToken);
            await ApplyMappingsAsync(db, package, targetProjectCode, selected, result, cancellationToken);
            await ApplyDictsAsync(db, package, targetProjectCode, selected, result, cancellationToken);
            await ApplyProjectConfigsAsync(db, package, targetProjectCode, selected, result, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            _configService.ClearCache();
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task AppendInterfacePreviewAsync(
        DataSyncDbContext db,
        ConfigSyncPackage package,
        string targetProjectCode,
        ConfigSyncPreviewResult result,
        CancellationToken cancellationToken)
    {
        if (package.Interfaces.Count == 0)
            return;

        var localInterfaces = await db.EsbInterfaceConfigs
            .Where(c => c.IntegrationProjectCode == targetProjectCode)
            .ToListAsync(cancellationToken);
        var localMap = localInterfaces.ToDictionary(c => c.TranCode, StringComparer.OrdinalIgnoreCase);

        foreach (var item in package.Interfaces)
        {
            var local = localMap.GetValueOrDefault(item.TranCode);
            var localHash = local == null
                ? ""
                : ComputeInterfaceHash(ToPackageInterface(
                    local,
                    await LoadInterfaceFilterRulesAsync(db, local.TranCode, targetProjectCode, cancellationToken),
                    await LoadInterfaceMatchRulesAsync(db, local.TranCode, targetProjectCode, cancellationToken),
                    await LoadIdempotentKeyPartsAsync(db, local.TranCode, targetProjectCode, cancellationToken)));
            var changeType = local == null
                ? ConfigSyncChangeType.New
                : string.Equals(localHash, item.ContentHash, StringComparison.Ordinal)
                    ? ConfigSyncChangeType.Identical
                    : ConfigSyncChangeType.Conflict;

            result.Items.Add(new ConfigSyncPreviewItem
            {
                ItemKey = BuildInterfaceItemKey(item.TranCode),
                ItemType = ConfigSyncItemType.Interface,
                ChangeType = changeType,
                Name = $"{item.TranCode} {item.TranName}".Trim(),
                Detail = $"过滤 {item.FilterRules.Count}；识别 {item.MatchRules.Count}；幂等 {item.IdempotentKeyParts.Count}",
                LocalSummary = local == null ? "本地不存在" : BuildInterfaceSummary(local),
                PackageSummary = BuildInterfaceSummary(item),
                IsSelected = changeType == ConfigSyncChangeType.New
            });
        }
    }

    private static async Task AppendMappingPreviewAsync(
        DataSyncDbContext db,
        ConfigSyncPackage package,
        string targetProjectCode,
        ConfigSyncPreviewResult result,
        TargetNameCatalog nameCatalog,
        CancellationToken cancellationToken)
    {
        if (package.FieldMappings.Count == 0)
            return;

        var localMappings = await db.EsbFieldMappings
            .Where(m => m.IntegrationProjectCode == targetProjectCode)
            .ToListAsync(cancellationToken);
        var mappingIds = localMappings.Select(m => m.Id).ToList();
        var localRules = mappingIds.Count == 0
            ? new List<EsbFilterRule>()
            : await db.EsbFilterRules
                .Where(r => r.MappingId != null && mappingIds.Contains(r.MappingId.Value))
                .OrderBy(r => r.MappingId)
                .ThenBy(r => r.RuleGroup)
                .ThenBy(r => r.SortOrder)
                .ToListAsync(cancellationToken);
        var ruleMap = localRules
            .GroupBy(r => r.MappingId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(ToPackageRule).ToList());
        var bySyncKey = localMappings
            .Where(m => !string.IsNullOrWhiteSpace(m.SyncKey))
            .ToDictionary(m => m.SyncKey!, StringComparer.OrdinalIgnoreCase);

        foreach (var item in package.FieldMappings)
        {
            var local = ResolveLocalMapping(localMappings, bySyncKey, item);
            var localDto = local == null ? null : ToPackageMapping(local, ruleMap.GetValueOrDefault(local.Id) ?? [], nameCatalog);
            var localHash = localDto == null ? "" : ComputeMappingHash(localDto);
            var changeType = GetMappingChangeType(local, localHash, item);

            result.Items.Add(new ConfigSyncPreviewItem
            {
                ItemKey = BuildMappingItemKey(item.SyncKey),
                ItemType = ConfigSyncItemType.FieldMapping,
                ChangeType = changeType,
                Name = $"{item.TranCode} → {GetMappingTargetText(item, nameCatalog)}",
                Detail = $"源路径：{(string.IsNullOrWhiteSpace(item.SourcePath) ? "默认值" : item.SourcePath)}；过滤 {item.FilterRules.Count}",
                LocalSummary = localDto == null ? "本地不存在" : BuildMappingSummary(localDto, nameCatalog),
                PackageSummary = BuildMappingSummary(item, nameCatalog),
                IsSelected = changeType is ConfigSyncChangeType.New or ConfigSyncChangeType.SafeUpdate
            });
        }
    }

    private static async Task AppendDictPreviewAsync(
        DataSyncDbContext db,
        ConfigSyncPackage package,
        string targetProjectCode,
        ConfigSyncPreviewResult result,
        CancellationToken cancellationToken)
    {
        if (package.DictEntries.Count == 0)
            return;

        var localDicts = await db.EsbDicts
            .Where(d => d.IntegrationProjectCode == targetProjectCode)
            .ToListAsync(cancellationToken);
        var localMap = localDicts.ToDictionary(d => BuildDictBusinessKey(d.DictCode, d.SourceValue), StringComparer.Ordinal);

        foreach (var item in package.DictEntries)
        {
            var local = localMap.GetValueOrDefault(BuildDictBusinessKey(item.DictCode, item.SourceValue));
            var localHash = local == null ? "" : ComputeDictHash(ToPackageDict(local));
            var changeType = local == null
                ? ConfigSyncChangeType.New
                : string.Equals(localHash, item.ContentHash, StringComparison.Ordinal)
                    ? ConfigSyncChangeType.Identical
                    : ConfigSyncChangeType.Conflict;

            result.Items.Add(new ConfigSyncPreviewItem
            {
                ItemKey = BuildDictItemKey(item.DictCode, item.SourceValue),
                ItemType = ConfigSyncItemType.Dictionary,
                ChangeType = changeType,
                Name = $"{item.DictCode} / {item.SourceValue}",
                Detail = $"目标值：{item.TargetValue}",
                LocalSummary = local == null ? "本地不存在" : $"{local.SourceValue} → {local.TargetValue}",
                PackageSummary = $"{item.SourceValue} → {item.TargetValue}",
                IsSelected = changeType == ConfigSyncChangeType.New
            });
        }
    }

    private static async Task AppendProjectConfigPreviewAsync(
        DataSyncDbContext db,
        ConfigSyncPackage package,
        string targetProjectCode,
        ConfigSyncPreviewResult result,
        CancellationToken cancellationToken)
    {
        if (package.ProjectConfigs.Count == 0)
            return;

        var localConfigs = await db.EsbIntegrationProjectConfigs
            .Where(c => c.IntegrationProjectCode == targetProjectCode)
            .ToListAsync(cancellationToken);
        var localMap = localConfigs.ToDictionary(c => c.ConfigKey, StringComparer.OrdinalIgnoreCase);

        foreach (var item in package.ProjectConfigs)
        {
            var local = localMap.GetValueOrDefault(item.ConfigKey);
            var localHash = local == null ? "" : ComputeProjectConfigHash(ToPackageProjectConfig(local));
            var changeType = local == null
                ? ConfigSyncChangeType.New
                : string.Equals(localHash, item.ContentHash, StringComparison.Ordinal)
                    ? ConfigSyncChangeType.Identical
                    : ConfigSyncChangeType.Conflict;

            result.Items.Add(new ConfigSyncPreviewItem
            {
                ItemKey = BuildProjectConfigItemKey(item.ConfigKey),
                ItemType = ConfigSyncItemType.ProjectConfig,
                ChangeType = changeType,
                Name = item.ConfigKey,
                Detail = item.Description ?? "",
                LocalSummary = local == null ? "本地不存在" : local.ConfigValue ?? "",
                PackageSummary = item.ConfigValue ?? "",
                IsSelected = changeType == ConfigSyncChangeType.New
            });
        }
    }

    private static async Task ApplyInterfacesAsync(
        DataSyncDbContext db,
        ConfigSyncPackage package,
        string targetProjectCode,
        HashSet<string> selected,
        ConfigSyncApplyResult result,
        CancellationToken cancellationToken)
    {
        foreach (var item in package.Interfaces.Where(i => selected.Contains(BuildInterfaceItemKey(i.TranCode))))
        {
            var existing = await db.EsbInterfaceConfigs
                .FirstOrDefaultAsync(c => c.IntegrationProjectCode == targetProjectCode && c.TranCode == item.TranCode, cancellationToken);
            if (existing == null)
            {
                existing = new EsbInterfaceConfig
                {
                    TranCode = item.TranCode,
                    IntegrationProjectCode = targetProjectCode
                };
                db.EsbInterfaceConfigs.Add(existing);
                result.Inserted++;
            }
            else
            {
                result.Updated++;
            }

            ApplyInterfaceValues(existing, item, targetProjectCode);
            await db.SaveChangesAsync(cancellationToken);

            var oldFilterRules = await db.EsbFilterRules
                .Where(r => r.IntegrationProjectCode == targetProjectCode && r.TranCode == item.TranCode && r.MappingId == null)
                .ToListAsync(cancellationToken);
            db.EsbFilterRules.RemoveRange(oldFilterRules);
            foreach (var rule in item.FilterRules.Select((rule, index) => ToEntityRule(rule, item.TranCode, targetProjectCode, null, index)))
                db.EsbFilterRules.Add(rule);

            var oldMatchRules = await db.EsbInterfaceMatchRules
                .Where(r => r.IntegrationProjectCode == targetProjectCode && r.TranCode == item.TranCode)
                .ToListAsync(cancellationToken);
            db.EsbInterfaceMatchRules.RemoveRange(oldMatchRules);
            foreach (var rule in item.MatchRules.Select((rule, index) => ToEntityMatchRule(rule, item.TranCode, targetProjectCode, index)))
                db.EsbInterfaceMatchRules.Add(rule);

            var oldParts = await db.EsbIdempotentKeyParts
                .Where(p => p.IntegrationProjectCode == targetProjectCode && p.TranCode == item.TranCode)
                .ToListAsync(cancellationToken);
            db.EsbIdempotentKeyParts.RemoveRange(oldParts);
            foreach (var part in item.IdempotentKeyParts.Select((part, index) => ToEntityIdempotentPart(part, item.TranCode, targetProjectCode, index)))
                db.EsbIdempotentKeyParts.Add(part);

            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static async Task ApplyMappingsAsync(
        DataSyncDbContext db,
        ConfigSyncPackage package,
        string targetProjectCode,
        HashSet<string> selected,
        ConfigSyncApplyResult result,
        CancellationToken cancellationToken)
    {
        var localMappings = await db.EsbFieldMappings
            .Where(m => m.IntegrationProjectCode == targetProjectCode)
            .ToListAsync(cancellationToken);
        var bySyncKey = localMappings
            .Where(m => !string.IsNullOrWhiteSpace(m.SyncKey))
            .ToDictionary(m => m.SyncKey!, StringComparer.OrdinalIgnoreCase);

        foreach (var item in package.FieldMappings.Where(m => selected.Contains(BuildMappingItemKey(m.SyncKey))))
        {
            var existing = ResolveLocalMapping(localMappings, bySyncKey, item);
            if (existing == null)
            {
                existing = new EsbFieldMapping
                {
                    SyncKey = item.SyncKey,
                    IntegrationProjectCode = targetProjectCode
                };
                db.EsbFieldMappings.Add(existing);
                localMappings.Add(existing);
                bySyncKey[item.SyncKey] = existing;
                result.Inserted++;
            }
            else
            {
                result.Updated++;
            }

            ApplyMappingValues(existing, item, targetProjectCode);
            existing.LastSyncHash = item.ContentHash;
            await db.SaveChangesAsync(cancellationToken);

            var oldRules = await db.EsbFilterRules
                .Where(r => r.MappingId == existing.Id)
                .ToListAsync(cancellationToken);
            db.EsbFilterRules.RemoveRange(oldRules);
            foreach (var rule in item.FilterRules.Select((rule, index) => ToEntityRule(rule, existing.TranCode, targetProjectCode, existing.Id, index)))
                db.EsbFilterRules.Add(rule);

            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static async Task ApplyDictsAsync(
        DataSyncDbContext db,
        ConfigSyncPackage package,
        string targetProjectCode,
        HashSet<string> selected,
        ConfigSyncApplyResult result,
        CancellationToken cancellationToken)
    {
        foreach (var item in package.DictEntries.Where(d => selected.Contains(BuildDictItemKey(d.DictCode, d.SourceValue))))
        {
            var existing = await db.EsbDicts
                .FirstOrDefaultAsync(d => d.IntegrationProjectCode == targetProjectCode
                                          && d.DictCode == item.DictCode
                                          && d.SourceValue == item.SourceValue, cancellationToken);
            if (existing == null)
            {
                existing = new EsbDict
                {
                    DictCode = item.DictCode,
                    SourceValue = item.SourceValue,
                    IntegrationProjectCode = targetProjectCode
                };
                db.EsbDicts.Add(existing);
                result.Inserted++;
            }
            else
            {
                result.Updated++;
            }

            existing.TargetValue = item.TargetValue;
            existing.SortOrder = item.SortOrder;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task ApplyProjectConfigsAsync(
        DataSyncDbContext db,
        ConfigSyncPackage package,
        string targetProjectCode,
        HashSet<string> selected,
        ConfigSyncApplyResult result,
        CancellationToken cancellationToken)
    {
        foreach (var item in package.ProjectConfigs.Where(c => selected.Contains(BuildProjectConfigItemKey(c.ConfigKey))))
        {
            var existing = await db.EsbIntegrationProjectConfigs
                .FirstOrDefaultAsync(c => c.IntegrationProjectCode == targetProjectCode && c.ConfigKey == item.ConfigKey, cancellationToken);
            if (existing == null)
            {
                existing = new EsbIntegrationProjectConfig
                {
                    ConfigKey = item.ConfigKey,
                    IntegrationProjectCode = targetProjectCode,
                    CreatedAt = DateTime.Now
                };
                db.EsbIntegrationProjectConfigs.Add(existing);
                result.Inserted++;
            }
            else
            {
                result.Updated++;
            }

            existing.ConfigValue = item.ConfigValue;
            existing.ConfigType = item.ConfigType;
            existing.Description = item.Description;
            existing.UpdatedAt = DateTime.Now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static ConfigSyncFieldMapping ToPackageMapping(
        EsbFieldMapping mapping,
        List<ConfigSyncFilterRule> rules,
        TargetNameCatalog? nameCatalog = null) => new()
    {
        SyncKey = mapping.SyncKey ?? "",
        TranCode = mapping.TranCode,
        MappingTarget = mapping.MappingTarget,
        SourcePath = mapping.SourcePath,
        TargetField = mapping.TargetField,
        TargetFieldName = nameCatalog == null ? null : ResolveTargetFieldName(mapping.MappingTarget, mapping.TargetField, nameCatalog),
        CardId = mapping.CardId,
        CardName = mapping.CardId.HasValue && nameCatalog?.CardNames.TryGetValue(mapping.CardId.Value, out var cardName) == true
            ? cardName
            : null,
        ArrayPath = mapping.ArrayPath,
        DictCode = mapping.DictCode,
        DictMatchMode = EsbFieldMapping.NormalizeDictMatchMode(mapping.DictMatchMode),
        DefaultValue = mapping.DefaultValue,
        ValueExpression = mapping.ValueExpression,
        IsRequired = mapping.IsRequired,
        IsEnabled = mapping.IsEnabled,
        SortOrder = mapping.SortOrder,
        Description = mapping.Description,
        FilterRules = rules
    };

    private static ConfigSyncInterfaceConfig ToPackageInterface(
        EsbInterfaceConfig config,
        List<ConfigSyncFilterRule> filterRules,
        List<ConfigSyncInterfaceMatchRule> matchRules,
        List<ConfigSyncIdempotentKeyPart> idempotentParts) => new()
    {
        TranCode = config.TranCode,
        TranName = config.TranName,
        IsEnabled = config.IsEnabled,
        HandlerType = config.HandlerType,
        HandlerName = config.HandlerName,
        AllowMultipleMatch = config.AllowMultipleMatch,
        ReceiveMode = config.ReceiveMode,
        LicenseCode = config.LicenseCode,
        EventTypeName = config.EventTypeName,
        Description = config.Description,
        SortOrder = config.SortOrder,
        MrnSourcePath = config.MrnSourcePath,
        EventStartTimeSourcePath = config.EventStartTimeSourcePath,
        VisitNoSourcePath = config.VisitNoSourcePath,
        InpatientNoSourcePath = config.InpatientNoSourcePath,
        SourceMessageIdPath = config.SourceMessageIdPath,
        MainRecordArrayPath = config.MainRecordArrayPath,
        AllowMissingEventTime = config.AllowMissingEventTime,
        ResponseMode = config.ResponseMode,
        MissingEventIdentityPolicy = config.MissingEventIdentityPolicy,
        MedicalRecordSyncRole = config.MedicalRecordSyncRole,
        SoapEnabled = config.SoapEnabled,
        SoapServiceCode = config.SoapServiceCode,
        SoapOperation = config.SoapOperation,
        SoapAction = config.SoapAction,
        SampleJson = config.SampleJson,
        FilterRules = filterRules,
        MatchRules = matchRules,
        IdempotentKeyParts = idempotentParts
    };

    private static ConfigSyncFilterRule ToPackageRule(EsbFilterRule rule) => new()
    {
        SourcePath = rule.SourcePath,
        Operator = rule.Operator,
        CompareValue = rule.CompareValue,
        RuleGroup = rule.RuleGroup,
        FilterScope = rule.FilterScope,
        IsEnabled = rule.IsEnabled,
        SortOrder = rule.SortOrder,
        Description = rule.Description
    };

    private static ConfigSyncInterfaceMatchRule ToPackageMatchRule(EsbInterfaceMatchRule rule) => new()
    {
        MatchGroup = rule.MatchGroup,
        SourcePath = rule.SourcePath,
        Operator = rule.Operator,
        CompareValue = rule.CompareValue,
        IsEnabled = rule.IsEnabled,
        SortOrder = rule.SortOrder,
        Description = rule.Description
    };

    private static ConfigSyncIdempotentKeyPart ToPackageIdempotentPart(EsbIdempotentKeyPart part) => new()
    {
        SourcePath = part.SourcePath,
        LiteralValue = part.LiteralValue,
        DefaultValue = part.DefaultValue,
        SortOrder = part.SortOrder,
        IsEnabled = part.IsEnabled,
        Description = part.Description
    };

    private static ConfigSyncDictEntry ToPackageDict(EsbDict dict) => new()
    {
        DictCode = dict.DictCode,
        SourceValue = dict.SourceValue,
        TargetValue = dict.TargetValue,
        SortOrder = dict.SortOrder
    };

    private static ConfigSyncProjectConfig ToPackageProjectConfig(EsbIntegrationProjectConfig config) => new()
    {
        ConfigKey = config.ConfigKey,
        ConfigValue = config.ConfigValue,
        ConfigType = config.ConfigType,
        Description = config.Description
    };

    private static void ApplyInterfaceValues(EsbInterfaceConfig entity, ConfigSyncInterfaceConfig item, string targetProjectCode)
    {
        entity.TranCode = item.TranCode;
        entity.IntegrationProjectCode = targetProjectCode;
        entity.TranName = item.TranName;
        entity.IsEnabled = item.IsEnabled;
        entity.HandlerType = item.HandlerType;
        entity.HandlerName = item.HandlerName;
        entity.AllowMultipleMatch = item.AllowMultipleMatch;
        entity.ReceiveMode = item.ReceiveMode;
        entity.LicenseCode = item.LicenseCode;
        entity.EventTypeName = item.EventTypeName;
        entity.Description = item.Description;
        entity.SortOrder = item.SortOrder;
        entity.MrnSourcePath = item.MrnSourcePath;
        entity.EventStartTimeSourcePath = item.EventStartTimeSourcePath;
        entity.VisitNoSourcePath = item.VisitNoSourcePath;
        entity.InpatientNoSourcePath = item.InpatientNoSourcePath;
        entity.SourceMessageIdPath = item.SourceMessageIdPath;
        entity.MainRecordArrayPath = item.MainRecordArrayPath;
        entity.AllowMissingEventTime = item.AllowMissingEventTime;
        entity.ResponseMode = item.ResponseMode;
        entity.MissingEventIdentityPolicy = item.MissingEventIdentityPolicy;
        entity.MedicalRecordSyncRole = item.MedicalRecordSyncRole;
        entity.SoapEnabled = item.SoapEnabled;
        entity.SoapServiceCode = item.SoapServiceCode;
        entity.SoapOperation = item.SoapOperation;
        entity.SoapAction = item.SoapAction;
        entity.SampleJson = item.SampleJson;
    }

    private static void ApplyMappingValues(EsbFieldMapping entity, ConfigSyncFieldMapping item, string targetProjectCode)
    {
        entity.SyncKey = item.SyncKey;
        entity.TranCode = item.TranCode;
        entity.IntegrationProjectCode = targetProjectCode;
        entity.MappingTarget = item.MappingTarget;
        entity.SourcePath = item.SourcePath;
        entity.TargetField = item.TargetField;
        entity.CardId = item.CardId;
        entity.ArrayPath = item.ArrayPath;
        entity.DictCode = item.DictCode;
        entity.DictMatchMode = EsbFieldMapping.NormalizeDictMatchMode(item.DictMatchMode);
        entity.DefaultValue = item.DefaultValue;
        entity.ValueExpression = item.ValueExpression;
        entity.IsRequired = item.IsRequired;
        entity.IsEnabled = item.IsEnabled;
        entity.SortOrder = item.SortOrder;
        entity.Description = item.Description;
    }

    private static EsbFilterRule ToEntityRule(ConfigSyncFilterRule rule, string tranCode, string targetProjectCode, int? mappingId, int index) => new()
    {
        TranCode = tranCode,
        IntegrationProjectCode = targetProjectCode,
        SourcePath = rule.SourcePath,
        Operator = string.IsNullOrWhiteSpace(rule.Operator) ? "eq" : rule.Operator,
        CompareValue = rule.CompareValue ?? "",
        RuleGroup = Math.Max(1, rule.RuleGroup),
        MappingId = mappingId,
        FilterScope = rule.FilterScope,
        IsEnabled = rule.IsEnabled,
        SortOrder = rule.SortOrder == 0 ? index : rule.SortOrder,
        Description = rule.Description
    };

    private static EsbInterfaceMatchRule ToEntityMatchRule(ConfigSyncInterfaceMatchRule rule, string tranCode, string targetProjectCode, int index) => new()
    {
        TranCode = tranCode,
        IntegrationProjectCode = targetProjectCode,
        MatchGroup = rule.MatchGroup <= 0 ? 1 : rule.MatchGroup,
        SourcePath = rule.SourcePath,
        Operator = string.IsNullOrWhiteSpace(rule.Operator) ? "eq" : rule.Operator,
        CompareValue = rule.CompareValue ?? "",
        IsEnabled = rule.IsEnabled,
        SortOrder = rule.SortOrder == 0 ? index : rule.SortOrder,
        Description = rule.Description
    };

    private static EsbIdempotentKeyPart ToEntityIdempotentPart(ConfigSyncIdempotentKeyPart part, string tranCode, string targetProjectCode, int index) => new()
    {
        TranCode = tranCode,
        IntegrationProjectCode = targetProjectCode,
        SourcePath = part.SourcePath,
        LiteralValue = part.LiteralValue,
        DefaultValue = part.DefaultValue,
        IsEnabled = part.IsEnabled,
        SortOrder = part.SortOrder == 0 ? index : part.SortOrder,
        Description = part.Description
    };

    private static async Task<List<ConfigSyncFilterRule>> LoadInterfaceFilterRulesAsync(
        DataSyncDbContext db,
        string tranCode,
        string targetProjectCode,
        CancellationToken cancellationToken)
    {
        var rules = await db.EsbFilterRules
            .Where(r => r.IntegrationProjectCode == targetProjectCode && r.TranCode == tranCode && r.MappingId == null)
            .OrderBy(r => r.RuleGroup)
            .ThenBy(r => r.SortOrder)
            .ToListAsync(cancellationToken);

        return rules.Select(ToPackageRule).ToList();
    }

    private static async Task<List<ConfigSyncInterfaceMatchRule>> LoadInterfaceMatchRulesAsync(
        DataSyncDbContext db,
        string tranCode,
        string targetProjectCode,
        CancellationToken cancellationToken)
    {
        var rules = await db.EsbInterfaceMatchRules
            .Where(r => r.IntegrationProjectCode == targetProjectCode && r.TranCode == tranCode)
            .OrderBy(r => r.MatchGroup)
            .ThenBy(r => r.SortOrder)
            .ToListAsync(cancellationToken);

        return rules.Select(ToPackageMatchRule).ToList();
    }

    private static async Task<List<ConfigSyncIdempotentKeyPart>> LoadIdempotentKeyPartsAsync(
        DataSyncDbContext db,
        string tranCode,
        string targetProjectCode,
        CancellationToken cancellationToken)
    {
        var parts = await db.EsbIdempotentKeyParts
            .Where(p => p.IntegrationProjectCode == targetProjectCode && p.TranCode == tranCode)
            .OrderBy(p => p.SortOrder)
            .ToListAsync(cancellationToken);

        return parts.Select(ToPackageIdempotentPart).ToList();
    }

    private static EsbFieldMapping? ResolveLocalMapping(
        List<EsbFieldMapping> localMappings,
        Dictionary<string, EsbFieldMapping> bySyncKey,
        ConfigSyncFieldMapping item)
    {
        if (!string.IsNullOrWhiteSpace(item.SyncKey) && bySyncKey.TryGetValue(item.SyncKey, out var byKey))
            return byKey;

        var candidates = localMappings
            .Where(m => string.Equals(m.TranCode, item.TranCode, StringComparison.OrdinalIgnoreCase)
                        && m.MappingTarget == item.MappingTarget
                        && string.Equals(m.TargetField, item.TargetField, StringComparison.OrdinalIgnoreCase)
                        && m.CardId == item.CardId)
            .ToList();

        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static ConfigSyncChangeType GetMappingChangeType(EsbFieldMapping? local, string localHash, ConfigSyncFieldMapping packageItem)
    {
        if (local == null)
            return ConfigSyncChangeType.New;

        if (string.Equals(localHash, packageItem.ContentHash, StringComparison.Ordinal))
            return string.IsNullOrWhiteSpace(local.SyncKey) || string.IsNullOrWhiteSpace(local.LastSyncHash)
                ? ConfigSyncChangeType.SafeUpdate
                : ConfigSyncChangeType.Identical;

        return string.Equals(local.LastSyncHash, localHash, StringComparison.Ordinal)
            ? ConfigSyncChangeType.SafeUpdate
            : ConfigSyncChangeType.Conflict;
    }

    private static void NormalizePackage(ConfigSyncPackage package)
    {
        foreach (var item in package.Interfaces)
            item.ContentHash = ComputeInterfaceHash(item);

        foreach (var item in package.FieldMappings)
        {
            if (string.IsNullOrWhiteSpace(item.SyncKey))
                throw new InvalidOperationException($"映射 {item.TranCode} → {item.TargetField} 缺少 syncKey。");

            item.DictMatchMode = EsbFieldMapping.NormalizeDictMatchMode(item.DictMatchMode);
            item.ContentHash = ComputeMappingHash(item);
        }

        foreach (var item in package.DictEntries)
            item.ContentHash = ComputeDictHash(item);

        foreach (var item in package.ProjectConfigs)
            item.ContentHash = ComputeProjectConfigHash(item);
    }

    private static string ComputeInterfaceHash(ConfigSyncInterfaceConfig item) => ComputeHash(new
    {
        item.TranCode,
        item.TranName,
        item.IsEnabled,
        item.HandlerType,
        item.HandlerName,
        item.AllowMultipleMatch,
        item.ReceiveMode,
        item.LicenseCode,
        item.EventTypeName,
        item.Description,
        item.SortOrder,
        item.MrnSourcePath,
        item.EventStartTimeSourcePath,
        item.VisitNoSourcePath,
        item.InpatientNoSourcePath,
        item.SourceMessageIdPath,
        item.MainRecordArrayPath,
        item.AllowMissingEventTime,
        item.ResponseMode,
        item.MissingEventIdentityPolicy,
        item.MedicalRecordSyncRole,
        item.SoapEnabled,
        item.SoapServiceCode,
        item.SoapOperation,
        item.SoapAction,
        item.SampleJson,
        FilterRules = OrderRules(item.FilterRules),
        MatchRules = item.MatchRules.OrderBy(r => r.MatchGroup).ThenBy(r => r.SortOrder).ThenBy(r => r.SourcePath),
        IdempotentKeyParts = item.IdempotentKeyParts.OrderBy(p => p.SortOrder).ThenBy(p => p.SourcePath).ThenBy(p => p.LiteralValue)
    });

    private static string ComputeMappingHash(ConfigSyncFieldMapping item) => ComputeHash(new
    {
        item.TranCode,
        item.MappingTarget,
        item.SourcePath,
        item.TargetField,
        item.CardId,
        item.ArrayPath,
        item.DictCode,
        DictMatchMode = EsbFieldMapping.NormalizeDictMatchMode(item.DictMatchMode),
        item.DefaultValue,
        item.ValueExpression,
        item.IsRequired,
        item.IsEnabled,
        item.SortOrder,
        item.Description,
        FilterRules = OrderRules(item.FilterRules)
    });

    private static string ComputeDictHash(ConfigSyncDictEntry item) => ComputeHash(new
    {
        item.DictCode,
        item.SourceValue,
        item.TargetValue,
        item.SortOrder
    });

    private static string ComputeProjectConfigHash(ConfigSyncProjectConfig item) => ComputeHash(new
    {
        item.ConfigKey,
        item.ConfigValue,
        item.ConfigType,
        item.Description
    });

    private static IOrderedEnumerable<ConfigSyncFilterRule> OrderRules(IEnumerable<ConfigSyncFilterRule> rules) =>
        rules.OrderBy(r => r.RuleGroup)
            .ThenBy(r => r.SortOrder)
            .ThenBy(r => r.SourcePath)
            .ThenBy(r => r.Operator)
            .ThenBy(r => r.CompareValue);

    private static string ComputeHash<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string BuildInterfaceSummary(EsbInterfaceConfig config) =>
        $"{config.TranName}；{config.HandlerType}；{(config.IsEnabled ? "启用" : "停用")}";

    private static string BuildInterfaceSummary(ConfigSyncInterfaceConfig config) =>
        $"{config.TranName}；{config.HandlerType}；{(config.IsEnabled ? "启用" : "停用")}";

    private static string BuildMappingSummary(ConfigSyncFieldMapping mapping, TargetNameCatalog nameCatalog) =>
        $"{GetMappingTargetText(mapping, nameCatalog)}；{mapping.SourcePath}；{(mapping.IsEnabled ? "启用" : "停用")}";

    private static string GetMappingTargetText(ConfigSyncFieldMapping mapping, TargetNameCatalog nameCatalog)
    {
        if (mapping.MappingTarget == MappingTarget.SubCard
            && string.Equals(mapping.TargetField, EsbFieldMapping.SubCardFilterTargetField, StringComparison.Ordinal))
        {
            var cardName = ResolveCardName(mapping, nameCatalog);
            return string.IsNullOrWhiteSpace(cardName)
                ? $"SubCard过滤：{mapping.CardId}"
                : $"SubCard过滤：{cardName}（{mapping.CardId}）";
        }

        var targetName = ResolvePackageTargetFieldName(mapping, nameCatalog);
        var targetText = string.IsNullOrWhiteSpace(targetName) || string.Equals(targetName, mapping.TargetField, StringComparison.OrdinalIgnoreCase)
            ? mapping.TargetField
            : $"{targetName}（{mapping.TargetField}）";

        if (mapping.MappingTarget != MappingTarget.SubCard)
            return $"{mapping.MappingTarget}：{targetText}";

        var subCardName = ResolveCardName(mapping, nameCatalog);
        return string.IsNullOrWhiteSpace(subCardName)
            ? $"SubCard：{targetText} / {mapping.CardId}"
            : $"SubCard：{subCardName} / {targetText}";
    }

    private async Task<TargetNameCatalog> BuildTargetNameCatalogAsync(
        string projectCode,
        ConfigSyncPackage? package,
        CancellationToken cancellationToken)
    {
        var catalog = new TargetNameCatalog();

        foreach (var field in PatientFieldCatalog.Definitions)
            catalog.PatientFieldNames[field.Name] = field.Label;

        catalog.EventFieldNames["event_start_time"] = "事件开始时间";
        catalog.EventFieldNames["event_end_time"] = "事件结束时间";

        var licenseCode = await _configService.GetDefaultLicenseCodeAsync(projectCode);
        if (string.IsNullOrWhiteSpace(licenseCode))
        {
            licenseCode = package?.Interfaces
                .Select(i => i.LicenseCode)
                .FirstOrDefault(code => !string.IsNullOrWhiteSpace(code));
        }

        if (!string.IsNullOrWhiteSpace(licenseCode))
        {
            try
            {
                var questionDict = await _bioCoreService.GetFormQuestionDictAsync(licenseCode);
                foreach (var (questionId, question) in questionDict)
                {
                    var info = FormBrowserHelper.BuildQuestionInfo(question);
                    catalog.QuestionNames[questionId.ToString()] = string.IsNullOrWhiteSpace(info.Title)
                        ? questionId.ToString()
                        : info.Title;
                }

                var cardDict = await _bioCoreService.GetAllCardListAsync(licenseCode);
                foreach (var (cardId, card) in cardDict)
                    catalog.CardNames[cardId] = card.Name;
            }
            catch
            {
                // 产品库不可用时仍允许同步，只回退到同步包自带名称或 ID。
            }
        }

        if (package != null)
        {
            foreach (var mapping in package.FieldMappings)
            {
                if (!string.IsNullOrWhiteSpace(mapping.TargetFieldName))
                    catalog.PackageFieldNames[BuildMappingFieldNameKey(mapping.MappingTarget, mapping.TargetField)] = mapping.TargetFieldName;

                if (mapping.CardId.HasValue && !string.IsNullOrWhiteSpace(mapping.CardName))
                    catalog.CardNames.TryAdd(mapping.CardId.Value, mapping.CardName);
            }
        }

        return catalog;
    }

    private static string? ResolvePackageTargetFieldName(ConfigSyncFieldMapping mapping, TargetNameCatalog nameCatalog)
    {
        var localName = ResolveTargetFieldName(mapping.MappingTarget, mapping.TargetField, nameCatalog);
        if (!string.IsNullOrWhiteSpace(localName))
            return localName;

        if (nameCatalog.PackageFieldNames.TryGetValue(BuildMappingFieldNameKey(mapping.MappingTarget, mapping.TargetField), out var packageName))
            return packageName;

        return mapping.TargetFieldName;
    }

    private static string? ResolveTargetFieldName(MappingTarget target, string targetField, TargetNameCatalog nameCatalog)
    {
        return target switch
        {
            MappingTarget.Patient => nameCatalog.PatientFieldNames.GetValueOrDefault(targetField),
            MappingTarget.Event => nameCatalog.EventFieldNames.GetValueOrDefault(targetField),
            MappingTarget.Question or MappingTarget.SubCard => nameCatalog.QuestionNames.GetValueOrDefault(targetField),
            _ => null
        };
    }

    private static string? ResolveCardName(ConfigSyncFieldMapping mapping, TargetNameCatalog nameCatalog)
    {
        if (mapping.CardId.HasValue && nameCatalog.CardNames.TryGetValue(mapping.CardId.Value, out var cardName))
            return cardName;

        return mapping.CardName;
    }

    private static string BuildMappingFieldNameKey(MappingTarget target, string targetField) =>
        $"{target}:{targetField}";

    private sealed class TargetNameCatalog
    {
        public Dictionary<string, string> PatientFieldNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> EventFieldNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> QuestionNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> PackageFieldNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<Guid, string> CardNames { get; } = [];
    }

    private static string BuildInterfaceItemKey(string tranCode) => $"interface:{tranCode}";

    private static string BuildMappingItemKey(string syncKey) => $"mapping:{syncKey}";

    private static string BuildDictItemKey(string dictCode, string sourceValue) =>
        $"dict:{BuildCompositeKey(dictCode, sourceValue)}";

    private static string BuildProjectConfigItemKey(string configKey) => $"project-config:{configKey}";

    private static string BuildDictBusinessKey(string dictCode, string sourceValue) =>
        BuildCompositeKey(dictCode, sourceValue);

    private static string BuildCompositeKey(params string?[] parts) =>
        string.Join("|", parts.Select(part => Convert.ToBase64String(Encoding.UTF8.GetBytes(part ?? ""))));
}
