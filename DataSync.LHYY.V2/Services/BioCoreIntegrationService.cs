using Bio.Core.FormSetDC;
using Bio.Core.Services;
using Bio.Models;
using DataSync.LHYY.V2.Models.Dto;
using Microsoft.EntityFrameworkCore;
using System.Reflection;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// Bio.Core 操作封装：患者/事件 CRUD + 表单导入
/// </summary>
public class BioCoreIntegrationService
{
    private readonly IDbContextFactory<CubeDbContext> _cubeDbContextFactory;
    private readonly IStaticDataService _staticDataService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<BioCoreIntegrationService> _logger;

    public BioCoreIntegrationService(
        IDbContextFactory<CubeDbContext> cubeDbContextFactory,
        IStaticDataService staticDataService,
        IServiceProvider serviceProvider,
        ILogger<BioCoreIntegrationService> logger)
    {
        _cubeDbContextFactory = cubeDbContextFactory;
        _staticDataService = staticDataService;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>
    /// 从 event_type_definition 获取项目下所有事件类型及其 FormSetId
    /// </summary>
    private List<(string Name, Guid FormSetId)> GetEventTypes(string licenseCode)
    {
        var license = (_staticDataService.LicenseList ?? []).FirstOrDefault(l => l.code == licenseCode);
        if (license == null) return [];

        return (_staticDataService.EventTypesList ?? [])
            .Where(e => e.project_id == license.project_id && e.form_set_id.HasValue)
            .Select(e => (e.name, e.form_set_id.GetValueOrDefault()))
            .ToList();
    }

    public async Task<List<LicenseInfo>> GetLicensesAsync()
    {
        await using var db = await _cubeDbContextFactory.CreateDbContextAsync();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT code, project_id
            FROM system.sys_license
            WHERE COALESCE(is_valid, TRUE) = TRUE
            ORDER BY code
            """;

        var result = new List<LicenseInfo>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
                continue;

            result.Add(new LicenseInfo
            {
                Code = reader.GetString(0),
                ProjectId = reader.GetGuid(1).ToString(),
            });
        }

        return result;
    }

    public async Task<List<HospitalInfo>> GetHospitalsAsync()
    {
        await using var db = await _cubeDbContextFactory.CreateDbContextAsync();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT h.id,
                   h.name,
                   count(DISTINCT p.id) AS project_count,
                   count(DISTINCT e.id) AS event_type_count
            FROM system.sys_hospital h
            LEFT JOIN form.form_project p ON p.hospital_id = h.id
            LEFT JOIN care.event_type_definition e
                   ON e.project_id = p.id
                  AND e.form_set_id IS NOT NULL
                  AND COALESCE(e.is_valid, TRUE) = TRUE
            GROUP BY h.id, h.name
            ORDER BY h.name
            """;

        var result = new List<HospitalInfo>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new HospitalInfo
            {
                Id = reader.GetGuid(0),
                Name = reader.IsDBNull(1) ? "" : reader.GetString(1),
                ProjectCount = Convert.ToInt32(reader.GetInt64(2)),
                EventTypeCount = Convert.ToInt32(reader.GetInt64(3)),
            });
        }

        return result;
    }

    public async Task<List<ProductProjectInfo>> GetProjectsByHospitalAsync(Guid hospitalId)
    {
        await using var db = await _cubeDbContextFactory.CreateDbContextAsync();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT p.id,
                   COALESCE(NULLIF(p.display_name, ''), p.name, p.id::text) AS project_name,
                   COALESCE(
                       string_agg(DISTINCT l.code, ', ' ORDER BY l.code)
                           FILTER (WHERE l.code IS NOT NULL AND l.code <> ''),
                       ''
                   ) AS license_codes,
                   count(DISTINCT fs.id) AS form_set_count,
                   count(DISTINCT e.id) AS event_type_count
            FROM form.form_project p
            LEFT JOIN system.sys_license l
                   ON l.project_id = p.id
                  AND COALESCE(l.is_valid, TRUE) = TRUE
            LEFT JOIN form.form_form_set fs
                   ON fs.project_id = p.id
                  AND fs.hospital_id = p.hospital_id
            LEFT JOIN care.event_type_definition e
                   ON e.project_id = p.id
                  AND e.form_set_id = fs.id
                  AND COALESCE(e.is_valid, TRUE) = TRUE
            WHERE p.hospital_id = @hospitalId
            GROUP BY p.id, p.display_name, p.name
            ORDER BY project_name
            """;

        var param = cmd.CreateParameter();
        param.ParameterName = "@hospitalId";
        param.Value = hospitalId;
        cmd.Parameters.Add(param);

        var result = new List<ProductProjectInfo>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new ProductProjectInfo
            {
                Id = reader.GetGuid(0),
                Name = reader.IsDBNull(1) ? "" : reader.GetString(1),
                LicenseCodes = reader.IsDBNull(2) ? "" : reader.GetString(2),
                FormSetCount = Convert.ToInt32(reader.GetInt64(3)),
                EventTypeCount = Convert.ToInt32(reader.GetInt64(4)),
            });
        }

        return result;
    }

    public async Task<List<ProductEventTypeInfo>> GetEventTypesByProjectAsync(Guid projectId)
    {
        await using var db = await _cubeDbContextFactory.CreateDbContextAsync();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT e.id,
                   e.name,
                   COALESCE(e."group", '') AS event_group,
                   e.form_set_id,
                   COALESCE(NULLIF(e.form_set_name, ''), fs.name, e.form_set_id::text) AS form_set_name,
                   COALESCE(NULLIF(e.project_name, ''), fs.project_name, '') AS project_name,
                   COALESCE(NULLIF(fs.hospital_name, ''), '') AS hospital_name
            FROM care.event_type_definition e
            JOIN form.form_form_set fs ON fs.id = e.form_set_id
            WHERE e.project_id = @projectId
              AND e.form_set_id IS NOT NULL
              AND COALESCE(e.is_valid, TRUE) = TRUE
            ORDER BY e."group" NULLS LAST, e.name
            """;

        var param = cmd.CreateParameter();
        param.ParameterName = "@projectId";
        param.Value = projectId;
        cmd.Parameters.Add(param);

        var result = new List<ProductEventTypeInfo>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (reader.IsDBNull(1) || reader.IsDBNull(3))
                continue;

            result.Add(new ProductEventTypeInfo
            {
                Id = reader.GetGuid(0),
                Name = reader.GetString(1),
                Group = reader.IsDBNull(2) ? "" : reader.GetString(2),
                FormSetId = reader.GetGuid(3),
                FormSetName = reader.IsDBNull(4) ? "" : reader.GetString(4),
                ProjectName = reader.IsDBNull(5) ? "" : reader.GetString(5),
                HospitalName = reader.IsDBNull(6) ? "" : reader.GetString(6),
            });
        }

        return result;
    }

    public async Task<List<ProductEventTypeInfo>> GetProductEventTypesByLicenseAsync(string licenseCode)
    {
        if (string.IsNullOrWhiteSpace(licenseCode))
            return [];

        await using var db = await _cubeDbContextFactory.CreateDbContextAsync();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT e.id,
                   e.name,
                   COALESCE(e."group", '') AS event_group,
                   e.form_set_id,
                   COALESCE(NULLIF(e.form_set_name, ''), fs.name, e.form_set_id::text) AS form_set_name,
                   COALESCE(NULLIF(e.project_name, ''), NULLIF(fs.project_name, ''), p.display_name, p.name, '') AS project_name,
                   COALESCE(NULLIF(fs.hospital_name, ''), h.name, '') AS hospital_name
            FROM system.sys_license l
            JOIN care.event_type_definition e ON e.project_id = l.project_id
            JOIN form.form_form_set fs ON fs.id = e.form_set_id
            LEFT JOIN form.form_project p ON p.id = e.project_id
            LEFT JOIN system.sys_hospital h ON h.id = fs.hospital_id
            WHERE l.code = @licenseCode
              AND COALESCE(l.is_valid, TRUE) = TRUE
              AND e.form_set_id IS NOT NULL
              AND COALESCE(e.is_valid, TRUE) = TRUE
            ORDER BY e."group" NULLS LAST, e.name, form_set_name
            """;

        var param = cmd.CreateParameter();
        param.ParameterName = "@licenseCode";
        param.Value = licenseCode.Trim();
        cmd.Parameters.Add(param);

        var result = new List<ProductEventTypeInfo>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (reader.IsDBNull(1) || reader.IsDBNull(3))
                continue;

            result.Add(new ProductEventTypeInfo
            {
                Id = reader.GetGuid(0),
                Name = reader.GetString(1),
                Group = reader.IsDBNull(2) ? "" : reader.GetString(2),
                FormSetId = reader.GetGuid(3),
                FormSetName = reader.IsDBNull(4) ? "" : reader.GetString(4),
                ProjectName = reader.IsDBNull(5) ? "" : reader.GetString(5),
                HospitalName = reader.IsDBNull(6) ? "" : reader.GetString(6),
            });
        }

        return result;
    }

    public async Task<List<FormSetInfo>> GetFormSetsByLicenseAsync(string licenseCode)
    {
        if (string.IsNullOrWhiteSpace(licenseCode))
            return [];

        await using var db = await _cubeDbContextFactory.CreateDbContextAsync();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT fs.id, fs.name, fs.hospital_id, fs.project_id,
                   COALESCE(string_agg(e.name, ', ' ORDER BY e.name), '') AS event_types
            FROM system.sys_license l
            JOIN form.form_form_set fs ON fs.project_id = l.project_id
            LEFT JOIN care.event_type_definition e
                   ON e.project_id = fs.project_id
                  AND e.form_set_id = fs.id
                  AND COALESCE(e.is_valid, TRUE) = TRUE
            WHERE l.code = @licenseCode
            GROUP BY fs.id, fs.name, fs.hospital_id, fs.project_id
            ORDER BY fs.name
            """;
        var param = cmd.CreateParameter();
        param.ParameterName = "@licenseCode";
        param.Value = licenseCode;
        cmd.Parameters.Add(param);

        var result = new List<FormSetInfo>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new FormSetInfo
            {
                Id = reader.GetGuid(0).ToString(),
                Name = reader.IsDBNull(1) ? "" : reader.GetString(1),
                HospitalId = reader.GetGuid(2).ToString(),
                ProjectId = reader.GetGuid(3).ToString(),
                EventTypes = reader.IsDBNull(4) ? "" : reader.GetString(4),
            });
        }

        return result;
    }

    public async Task<List<(string Name, Guid FormSetId)>> GetEventTypesAsync(string licenseCode)
    {
        var cached = GetEventTypes(licenseCode);
        if (cached.Count > 0)
            return cached;

        return await QueryEventTypesAsync(licenseCode);
    }

    /// <summary>
    /// 根据 LicenseCode + EventTypeName 查找 FormSet
    /// </summary>
    private (form_form_set? FormSet, Guid HospitalId, Guid ProjectId) FindFormSet(string licenseCode, string eventTypeName)
    {
        var cached = TryFindFormSetInCache(licenseCode, eventTypeName);
        if (cached.FormSet != null)
            return cached;

        var license = (_staticDataService.LicenseList ?? []).FirstOrDefault(l => l.code == licenseCode);
        if (license == null)
        {
            _logger.LogWarning("未找到 LicenseCode: {LicenseCode}", licenseCode);
            return (null, Guid.Empty, Guid.Empty);
        }

        _logger.LogWarning("未找到 LicenseCode={LicenseCode} EventType={EventType} 对应的 FormSet",
            licenseCode, eventTypeName);
        return (null, Guid.Empty, Guid.Empty);
    }

    public async Task<(form_form_set? FormSet, Guid HospitalId, Guid ProjectId)> FindFormSetAsync(string licenseCode, string eventTypeName)
    {
        var cached = TryFindFormSetInCache(licenseCode, eventTypeName);
        if (cached.FormSet != null)
            return cached;

        return await QueryFormSetAsync(licenseCode, eventTypeName);
    }

    private (form_form_set? FormSet, Guid HospitalId, Guid ProjectId) TryFindFormSetInCache(string licenseCode, string eventTypeName)
    {
        var license = (_staticDataService.LicenseList ?? []).FirstOrDefault(l => l.code == licenseCode);
        if (license == null) return (null, Guid.Empty, Guid.Empty);

        var formSets = (_staticDataService.FormSetList ?? [])
            .Where(fs => fs.project_id == license.project_id)
            .ToList();

        var eventType = (_staticDataService.EventTypesList ?? [])
            .FirstOrDefault(e => e.project_id == license.project_id && e.name == eventTypeName);
        var formSet = formSets.FirstOrDefault(fs => fs.id == eventType?.form_set_id);

        return formSet == null
            ? (null, Guid.Empty, Guid.Empty)
            : (formSet, formSet.hospital_id, formSet.project_id);
    }

    /// <summary>
    /// 获取 FormSet 的问题字典（包含关联 FormSet 的问题）
    /// </summary>
    private Dictionary<Guid, form_question> GetFormQuestionDict(string licenseCode)
    {
        var license = (_staticDataService.LicenseList ?? []).FirstOrDefault(l => l.code == licenseCode);
        if (license == null) return [];

        var formSets = (_staticDataService.FormSetList ?? [])
            .Where(fs => fs.project_id == license.project_id)
            .ToList();

        var dict = new Dictionary<Guid, form_question>();
        foreach (var fs in formSets)
        {
            foreach (var q in fs.form_question ?? [])
            {
                dict.TryAdd(q.id, q);
            }
        }
        return dict;
    }

    public async Task<Dictionary<Guid, form_question>> GetFormQuestionDictAsync(string licenseCode)
    {
        var cached = GetFormQuestionDict(licenseCode);
        if (cached.Count > 0)
            return cached;

        var formSetIds = await QueryFormSetIdsByLicenseAsync(licenseCode);
        if (formSetIds.Count == 0)
            return [];

        await using var db = await _cubeDbContextFactory.CreateDbContextAsync();
        return await db.Set<form_question>()
            .AsNoTracking()
            .Where(q => formSetIds.Contains(q.form_set_id))
            .ToDictionaryAsync(q => q.id);
    }

    /// <summary>
    /// 获取 SubCard 卡片列表（type 为 multiple/table 的卡片）
    /// </summary>
    public async Task<List<CardInfo>> GetSubCardListAsync(string licenseCode)
    {
        var license = (_staticDataService.LicenseList ?? []).FirstOrDefault(l => l.code == licenseCode);
        if (license == null) return [];

        var formSetIds = (_staticDataService.FormSetList ?? [])
            .Where(fs => fs.project_id == license.project_id)
            .Select(fs => fs.id)
            .ToList();

        if (formSetIds.Count == 0)
            formSetIds = await QueryFormSetIdsByLicenseAsync(licenseCode);

        if (formSetIds.Count == 0) return [];

        await using var db = await _cubeDbContextFactory.CreateDbContextAsync();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        var paramList = string.Join(",", formSetIds.Select((_, i) => $"@p{i}"));
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id, name, parent_id, form_name, type, pre_uid FROM form.form_card WHERE form_set_id IN ({paramList}) AND type IN ('multiple', 'table')";

        for (var i = 0; i < formSetIds.Count; i++)
        {
            var param = cmd.CreateParameter();
            param.ParameterName = $"@p{i}";
            param.Value = formSetIds[i];
            cmd.Parameters.Add(param);
        }

        var result = new List<CardInfo>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new CardInfo
            {
                Id = reader.GetGuid(0),
                Name = reader.IsDBNull(1) ? "" : reader.GetString(1),
                ParentId = reader.IsDBNull(2) ? null : reader.GetGuid(2),
                FormName = reader.IsDBNull(3) ? "" : reader.GetString(3),
                CardType = reader.IsDBNull(4) ? "default" : reader.GetString(4),
                PreUid = reader.IsDBNull(5) ? null : reader.GetGuid(5),
            });
        }

        return result;
    }

    /// <summary>
    /// 获取所有卡片列表（不限 type），返回以 Id 为 Key 的字典
    /// </summary>
    public async Task<Dictionary<Guid, CardInfo>> GetAllCardListAsync(string licenseCode)
    {
        var license = (_staticDataService.LicenseList ?? []).FirstOrDefault(l => l.code == licenseCode);
        if (license == null) return [];

        var formSetIds = (_staticDataService.FormSetList ?? [])
            .Where(fs => fs.project_id == license.project_id)
            .Select(fs => fs.id)
            .ToList();

        if (formSetIds.Count == 0)
            formSetIds = await QueryFormSetIdsByLicenseAsync(licenseCode);

        if (formSetIds.Count == 0) return [];

        await using var db = await _cubeDbContextFactory.CreateDbContextAsync();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        var paramList = string.Join(",", formSetIds.Select((_, i) => $"@p{i}"));
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT id, name, parent_id, form_name, type, pre_uid FROM form.form_card WHERE form_set_id IN ({paramList})";

        for (var i = 0; i < formSetIds.Count; i++)
        {
            var param = cmd.CreateParameter();
            param.ParameterName = $"@p{i}";
            param.Value = formSetIds[i];
            cmd.Parameters.Add(param);
        }

        var result = new Dictionary<Guid, CardInfo>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetGuid(0);
            result[id] = new CardInfo
            {
                Id = id,
                Name = reader.IsDBNull(1) ? "" : reader.GetString(1),
                ParentId = reader.IsDBNull(2) ? null : reader.GetGuid(2),
                FormName = reader.IsDBNull(3) ? "" : reader.GetString(3),
                CardType = reader.IsDBNull(4) ? "default" : reader.GetString(4),
                PreUid = reader.IsDBNull(5) ? null : reader.GetGuid(5),
            };
        }

        return result;
    }

    /// <summary>
    /// 根据 FormSet ID 获取问题字典
    /// </summary>
    private Dictionary<Guid, form_question> GetFormQuestionDictByFormSet(Guid formSetId)
    {
        var formSet = (_staticDataService.FormSetList ?? []).FirstOrDefault(fs => fs.id == formSetId);
        if (formSet == null) return [];

        return (formSet.form_question ?? []).ToDictionary(q => q.id);
    }

    public async Task<Dictionary<Guid, form_question>> GetFormQuestionDictByFormSetAsync(Guid formSetId)
    {
        var cached = GetFormQuestionDictByFormSet(formSetId);
        if (cached.Count > 0)
            return cached;

        await using var db = await _cubeDbContextFactory.CreateDbContextAsync();
        return await db.Set<form_question>()
            .AsNoTracking()
            .Where(q => q.form_set_id == formSetId)
            .ToDictionaryAsync(q => q.id);
    }

    private async Task<List<(string Name, Guid FormSetId)>> QueryEventTypesAsync(string licenseCode)
    {
        if (string.IsNullOrWhiteSpace(licenseCode))
            return [];

        await using var db = await _cubeDbContextFactory.CreateDbContextAsync();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT e.name, e.form_set_id
            FROM system.sys_license l
            JOIN care.event_type_definition e ON e.project_id = l.project_id
            WHERE l.code = @licenseCode
              AND e.form_set_id IS NOT NULL
              AND COALESCE(e.is_valid, TRUE) = TRUE
            ORDER BY e.name
            """;
        var param = cmd.CreateParameter();
        param.ParameterName = "@licenseCode";
        param.Value = licenseCode;
        cmd.Parameters.Add(param);

        var result = new List<(string Name, Guid FormSetId)>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
                continue;

            result.Add((reader.GetString(0), reader.GetGuid(1)));
        }

        return result;
    }

    private async Task<List<Guid>> QueryFormSetIdsByLicenseAsync(string licenseCode)
    {
        if (string.IsNullOrWhiteSpace(licenseCode))
            return [];

        await using var db = await _cubeDbContextFactory.CreateDbContextAsync();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT fs.id
            FROM system.sys_license l
            JOIN form.form_form_set fs ON fs.project_id = l.project_id
            WHERE l.code = @licenseCode
            """;
        var param = cmd.CreateParameter();
        param.ParameterName = "@licenseCode";
        param.Value = licenseCode;
        cmd.Parameters.Add(param);

        var result = new List<Guid>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (!reader.IsDBNull(0))
                result.Add(reader.GetGuid(0));
        }

        return result;
    }

    private async Task<(form_form_set? FormSet, Guid HospitalId, Guid ProjectId)> QueryFormSetAsync(string licenseCode, string eventTypeName)
    {
        if (string.IsNullOrWhiteSpace(licenseCode) || string.IsNullOrWhiteSpace(eventTypeName))
            return (null, Guid.Empty, Guid.Empty);

        await using var db = await _cubeDbContextFactory.CreateDbContextAsync();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT fs.id, fs.name, fs.hospital_id, fs.project_id, fs.project_name
            FROM system.sys_license l
            JOIN care.event_type_definition e ON e.project_id = l.project_id
            JOIN form.form_form_set fs ON fs.id = e.form_set_id
            WHERE l.code = @licenseCode
              AND e.name = @eventTypeName
              AND e.form_set_id IS NOT NULL
              AND COALESCE(e.is_valid, TRUE) = TRUE
            LIMIT 1
            """;

        var licenseParam = cmd.CreateParameter();
        licenseParam.ParameterName = "@licenseCode";
        licenseParam.Value = licenseCode;
        cmd.Parameters.Add(licenseParam);

        var eventTypeParam = cmd.CreateParameter();
        eventTypeParam.ParameterName = "@eventTypeName";
        eventTypeParam.Value = eventTypeName;
        cmd.Parameters.Add(eventTypeParam);

        using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
            return (null, Guid.Empty, Guid.Empty);

        var formSet = new form_form_set
        {
            id = reader.GetGuid(0),
            name = reader.IsDBNull(1) ? "" : reader.GetString(1),
            hospital_id = reader.GetGuid(2),
            project_id = reader.GetGuid(3),
            project_name = reader.IsDBNull(4) ? "" : reader.GetString(4),
        };

        return (formSet, formSet.hospital_id, formSet.project_id);
    }

    /// <summary>
    /// 根据 FormSet ID 获取所有卡片列表
    /// </summary>
    public async Task<Dictionary<Guid, CardInfo>> GetAllCardListByFormSetAsync(Guid formSetId)
    {
        await using var db = await _cubeDbContextFactory.CreateDbContextAsync();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, parent_id, form_name, type, pre_uid FROM form.form_card WHERE form_set_id = @p0";
        var param = cmd.CreateParameter();
        param.ParameterName = "@p0";
        param.Value = formSetId;
        cmd.Parameters.Add(param);

        var result = new Dictionary<Guid, CardInfo>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetGuid(0);
            result[id] = new CardInfo
            {
                Id = id,
                Name = reader.IsDBNull(1) ? "" : reader.GetString(1),
                ParentId = reader.IsDBNull(2) ? null : reader.GetGuid(2),
                FormName = reader.IsDBNull(3) ? "" : reader.GetString(3),
                CardType = reader.IsDBNull(4) ? "default" : reader.GetString(4),
                PreUid = reader.IsDBNull(5) ? null : reader.GetGuid(5),
            };
        }

        return result;
    }

    /// <summary>
    /// 根据 FormSet ID 获取表单列表及排序
    /// </summary>
    public async Task<Dictionary<Guid, FormInfo>> GetFormListByFormSetAsync(Guid formSetId)
    {
        await using var db = await _cubeDbContextFactory.CreateDbContextAsync();
        var conn = db.Database.GetDbConnection();
        await conn.OpenAsync();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, sort_index FROM form.form_form WHERE form_set_id = @p0";
        var param = cmd.CreateParameter();
        param.ParameterName = "@p0";
        param.Value = formSetId;
        cmd.Parameters.Add(param);

        var result = new Dictionary<Guid, FormInfo>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var id = reader.GetGuid(0);
            result[id] = new FormInfo
            {
                Id = id,
                Name = reader.IsDBNull(1) ? "" : reader.GetString(1),
                SortIndex = reader.IsDBNull(2) ? int.MaxValue : reader.GetInt16(2),
            };
        }

        return result;
    }

    /// <summary>
    /// 根据病案号查找患者
    /// </summary>
    public async Task<patient?> GetPatientByMrnAsync(string medicalRecordNumber, Guid hospitalId, Guid projectId)
    {
        await using var db = await _cubeDbContextFactory.CreateDbContextAsync();
        return await db.Set<patient>()
            .FirstOrDefaultAsync(p =>
                p.medical_record_number == medicalRecordNumber &&
                p.hospital_id == hospitalId &&
                p.project_id == projectId &&
                p.is_valid == true);
    }

    public async Task<patient?> GetPatientByIdAsync(Guid patientId, Guid hospitalId, Guid projectId)
    {
        await using var db = await _cubeDbContextFactory.CreateDbContextAsync();
        return await db.Set<patient>()
            .FirstOrDefaultAsync(p =>
                p.id == patientId &&
                p.hospital_id == hospitalId &&
                p.project_id == projectId &&
                p.is_valid == true);
    }

    public async Task<patient_event?> GetEventByIdAsync(Guid eventId)
    {
        await using var db = await _cubeDbContextFactory.CreateDbContextAsync();
        return await db.Set<patient_event>()
            .FirstOrDefaultAsync(e => e.id == eventId && e.is_valid == true);
    }

    /// <summary>
    /// 查找已有事件，不创建患者事件或住院记录
    /// </summary>
    public async Task<patient_event?> GetExistingEventAsync(Guid patientId, Guid projectId, DateTime eventStartTime, string eventType)
    {
        await using var db = await _cubeDbContextFactory.CreateDbContextAsync();
        return await FindExistingEventAsync(db, patientId, projectId, eventStartTime.Date, eventType);
    }

    /// <summary>
    /// 创建患者（通过反射设置字段值），包含唯一约束冲突保护
    /// </summary>
    public async Task<patient> CreatePatientAsync(Dictionary<string, string?> fieldValues, Guid hospitalId, Guid projectId)
    {
        await using var db = await _cubeDbContextFactory.CreateDbContextAsync();

        var patientEntity = new patient();
        SetPatientFields(patientEntity, fieldValues);

        patientEntity.id = Guid.NewGuid();
        patientEntity.create_time = DateTime.Now;
        patientEntity.is_valid = true;
        patientEntity.source_type = "care";
        patientEntity.hospital_id = hospitalId;
        patientEntity.project_id = projectId;

        // 处理 unique_patient
        if (!string.IsNullOrWhiteSpace(patientEntity.sid_number))
        {
            var existingUnique = await db.unique_patient
                .FirstOrDefaultAsync(up => up.sid_number == patientEntity.sid_number);

            if (existingUnique != null)
            {
                patientEntity.unique_id = existingUnique.id;
            }
            else
            {
                var uniqueId = Guid.NewGuid();
                var uniquePatient = new unique_patient
                {
                    id = uniqueId,
                    name = patientEntity.name,
                    birthday = patientEntity.birthday,
                    gender = patientEntity.gender,
                    sid_type = patientEntity.sid_type,
                    sid_number = patientEntity.sid_number,
                };
                try
                {
                    await db.unique_patient.AddAsync(uniquePatient);
                    await db.SaveChangesAsync();
                    patientEntity.unique_id = uniqueId;
                }
                catch (DbUpdateException ex) when (IsUniqueViolation(ex))
                {
                    // unique_patient 并发冲突，重新查询
                    _logger.LogWarning("unique_patient 创建冲突，sid_number={SidNumber}，重新查询", patientEntity.sid_number);
                    db.ChangeTracker.Clear();
                    var existing = await db.unique_patient.FirstOrDefaultAsync(up => up.sid_number == patientEntity.sid_number);
                    if (existing != null)
                        patientEntity.unique_id = existing.id;
                }
            }
        }

        try
        {
            await db.Set<patient>().AddAsync(patientEntity);
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            _logger.LogWarning("患者创建冲突，MRN={MRN}，重新查询", patientEntity.medical_record_number);
            var existing = await GetPatientByMrnAsync(patientEntity.medical_record_number, hospitalId, projectId);
            if (existing != null) return existing;
            throw;
        }

        _logger.LogInformation("创建患者: {PatientId}, MRN: {MRN}", patientEntity.id, patientEntity.medical_record_number);
        return patientEntity;
    }

    /// <summary>
    /// 更新已有患者字段（跳过系统字段）
    /// </summary>
    public async Task UpdatePatientFieldsAsync(Guid patientId, Dictionary<string, string?> fieldValues)
    {
        await using var db = await _cubeDbContextFactory.CreateDbContextAsync();
        var entity = await db.Set<patient>().FindAsync(patientId);
        if (entity == null) return;

        var type = typeof(patient);
        var skipFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "id", "hospital_id", "project_id", "unique_id", "create_time", "is_valid", "source_type", "medical_record_number" };

        foreach (var (fieldName, value) in fieldValues)
        {
            if (string.IsNullOrEmpty(value) || skipFields.Contains(fieldName)) continue;

            var prop = type.GetProperty(fieldName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop != null && prop.CanWrite)
            {
                var converted = ConvertValue(value, prop.PropertyType);
                if (converted != null) prop.SetValue(entity, converted);
                continue;
            }

            var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (field != null)
            {
                var converted = ConvertValue(value, field.FieldType);
                if (converted != null) field.SetValue(entity, converted);
            }
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// 获取或创建事件
    /// </summary>
    public async Task<patient_event> GetOrCreateEventAsync(
        Guid patientId, Guid formSetId, Guid hospitalId, Guid projectId,
        string formSetName, DateTime eventStartTime, DateTime? eventEndTime, string eventType)
    {
        await using var db = await _cubeDbContextFactory.CreateDbContextAsync();
        var searchDate = eventStartTime.Date;

        var existingEvent = await FindExistingEventAsync(db, patientId, projectId, searchDate, eventType);
        if (existingEvent != null)
        {
            await ApplyExistingEventUpdateAsync(db, existingEvent, eventEndTime);
            _logger.LogInformation("找到已有事件: {EventId}", existingEvent.id);
            return existingEvent;
        }

        await using var transaction = await db.Database.BeginTransactionAsync();
        var lockKey = AdvisoryLockKeyHelper.Build(
            "patient-event",
            patientId.ToString("N"),
            projectId.ToString("N"),
            searchDate.Ticks.ToString(),
            eventType);
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock({0})",
            new object[] { lockKey });

        existingEvent = await FindExistingEventAsync(db, patientId, projectId, searchDate, eventType);
        if (existingEvent != null)
        {
            await ApplyExistingEventUpdateAsync(db, existingEvent, eventEndTime);
            await transaction.CommitAsync();
            _logger.LogInformation("找到已有事件: {EventId}", existingEvent.id);
            return existingEvent;
        }

        // 创建新事件
        var eventId = Guid.NewGuid();
        var eventStatus = eventEndTime.HasValue ? "已完成" : "未完成";
        var newEvent = new patient_event
        {
            id = eventId,
            patient_id = patientId,
            form_set_id = formSetId,
            form_set_name = formSetName,
            project_id = projectId,
            event_type = eventType,
            event_start_time = searchDate,
            event_end_time = eventEndTime?.Date,
            event_status = eventStatus,
            is_valid = true
        };

        var newHospitalized = new patient_hospitalized
        {
            id = Guid.NewGuid(),
            patient_id = patientId,
            patient_event_id = eventId,
            hospitalized_start_date = DateOnly.FromDateTime(searchDate),
            hospitalized_end_date = eventEndTime.HasValue ? DateOnly.FromDateTime(eventEndTime.Value.Date) : null
        };

        try
        {
            await db.Set<patient_event>().AddAsync(newEvent);
            await db.Set<patient_hospitalized>().AddAsync(newHospitalized);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            await transaction.RollbackAsync();
            _logger.LogWarning("事件创建冲突，PatientId={PatientId}, EventType={EventType}, EventStartTime={EventStartTime}，重新查询",
                patientId,
                eventType,
                searchDate);
            db.ChangeTracker.Clear();
            existingEvent = await FindExistingEventAsync(db, patientId, projectId, searchDate, eventType);
            if (existingEvent != null) return existingEvent;
            throw;
        }

        _logger.LogInformation("创建新事件: {EventId}, 类型: {EventType}", eventId, eventType);
        return newEvent;
    }

    private static Task<patient_event?> FindExistingEventAsync(
        CubeDbContext db,
        Guid patientId,
        Guid projectId,
        DateTime searchDate,
        string eventType)
    {
        return db.Set<patient_event>()
            .FirstOrDefaultAsync(e =>
                e.patient_id == patientId &&
                e.event_start_time == searchDate &&
                e.event_type == eventType &&
                e.is_valid == true &&
                e.project_id == projectId);
    }

    private static async Task ApplyExistingEventUpdateAsync(
        CubeDbContext db,
        patient_event existingEvent,
        DateTime? eventEndTime)
    {
        var updated = false;

        if (eventEndTime.HasValue)
        {
            if (existingEvent.event_end_time == null || existingEvent.event_end_time.Value != eventEndTime.Value.Date)
            {
                existingEvent.event_end_time = eventEndTime.Value.Date;
                updated = true;
            }

            var hospitalized = await db.Set<patient_hospitalized>()
                .FirstOrDefaultAsync(h => h.patient_event_id == existingEvent.id);
            if (hospitalized != null)
            {
                var newEndDate = DateOnly.FromDateTime(eventEndTime.Value.Date);
                if (hospitalized.hospitalized_end_date != newEndDate)
                {
                    hospitalized.hospitalized_end_date = newEndDate;
                    updated = true;
                }
            }

            if (existingEvent.event_status != "已完成")
            {
                existingEvent.event_status = "已完成";
                updated = true;
            }
        }

        if (updated)
            await db.SaveChangesAsync();
    }

    /// <summary>
    /// 创建新的 IFormsetImportService 实例并初始化
    /// </summary>
    public async Task<IFormsetImportService> CreateImportServiceAsync(Guid formSetId)
    {
        var importService = _serviceProvider.GetRequiredService<IFormsetImportService>();
        await importService.InitializeImportAsync(formSetId);
        return importService;
    }

    /// <summary>
    /// 通过反射设置 patient 字段/属性值
    /// </summary>
    private static void SetPatientFields(patient entity, Dictionary<string, string?> fieldValues)
    {
        var type = typeof(patient);
        foreach (var (fieldName, value) in fieldValues)
        {
            if (string.IsNullOrEmpty(value)) continue;

            // 先尝试属性
            var prop = type.GetProperty(fieldName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop != null && prop.CanWrite)
            {
                var converted = ConvertValue(value, prop.PropertyType);
                if (converted != null)
                    prop.SetValue(entity, converted);
                continue;
            }

            // 再尝试字段
            var field = type.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (field != null)
            {
                var converted = ConvertValue(value, field.FieldType);
                if (converted != null)
                    field.SetValue(entity, converted);
            }
        }
    }

    private static object? ConvertValue(string value, Type targetType)
    {
        try
        {
            var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

            if (underlying == typeof(string)) return value;
            if (underlying == typeof(Guid)) return Guid.Parse(value);
            if (underlying == typeof(DateTime)) return DateTime.Parse(value);
            if (underlying == typeof(DateOnly)) return DateOnly.Parse(value);
            if (underlying == typeof(int)) return int.Parse(value);
            if (underlying == typeof(bool)) return bool.Parse(value);
            if (underlying == typeof(decimal)) return decimal.Parse(value);
            // 支持 string[] 数组类型（逗号分隔）
            if (targetType == typeof(string[])) return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return Convert.ChangeType(value, underlying);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 判断是否为唯一约束冲突异常
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        var msg = ex.InnerException?.Message;
        if (msg == null) return false;
        return msg.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("unique", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("23505"); // PostgreSQL unique violation
    }
}
