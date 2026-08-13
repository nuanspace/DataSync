CREATE TABLE IF NOT EXISTS cyyy.api_platform_configs (
    id                            SERIAL PRIMARY KEY,
    name                          TEXT NOT NULL,
    base_url                      TEXT NOT NULL,
    auth_config                   JSONB NOT NULL DEFAULT '{}'::jsonb,
    query_config                  JSONB NOT NULL DEFAULT '{}'::jsonb,
    response_config               JSONB NOT NULL DEFAULT '{}'::jsonb,
    request_interval_milliseconds INT NOT NULL DEFAULT 200,
    ignore_ssl_errors             BOOLEAN NOT NULL DEFAULT FALSE,
    debug_log_enabled             BOOLEAN NOT NULL DEFAULT FALSE,
    enabled                       BOOLEAN NOT NULL DEFAULT TRUE,
    updated_at                    TIMESTAMP NOT NULL DEFAULT NOW()
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_api_platform_configs_name
    ON cyyy.api_platform_configs (name);

CREATE TABLE IF NOT EXISTS cyyy.api_interfaces (
    id              SERIAL PRIMARY KEY,
    api_platform_id INT NOT NULL,
    code            TEXT NOT NULL,
    name            TEXT NOT NULL,
    relative_path   TEXT NOT NULL DEFAULT ''
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_api_interfaces_platform_code_path
    ON cyyy.api_interfaces (api_platform_id, code, relative_path);

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint WHERE conname = 'fk_api_interfaces_platform'
    ) THEN
        ALTER TABLE cyyy.api_interfaces
            ADD CONSTRAINT fk_api_interfaces_platform
            FOREIGN KEY (api_platform_id) REFERENCES cyyy.api_platform_configs(id) ON DELETE RESTRICT;
    END IF;
END $$;

ALTER TABLE cyyy.sync_task_interfaces
    ADD COLUMN IF NOT EXISTS api_interface_id INT,
    ADD COLUMN IF NOT EXISTS query_mappings TEXT;

ALTER TABLE cyyy.ingestion_sources
    ADD COLUMN IF NOT EXISTS api_interface_id INT,
    ADD COLUMN IF NOT EXISTS query_mappings TEXT;

ALTER TABLE cyyy.sync_task_interfaces
    ALTER COLUMN source_type SET DEFAULT 'Api';

ALTER TABLE cyyy.ingestion_sources
    ALTER COLUMN source_type SET DEFAULT 'Api';

INSERT INTO cyyy.api_platform_configs (
    name, base_url, auth_config, query_config, response_config,
    request_interval_milliseconds, ignore_ssl_errors, debug_log_enabled, enabled, updated_at)
SELECT
    '数据湖',
    c."BaseUrl",
    jsonb_build_object(
        'TokenEndpoint', c."TokenEndpoint",
        'RequestType', 'Form',
        'Parameters', jsonb_build_array(
            jsonb_build_object('Name', 'grant_type', 'Value', 'client_credentials', 'ValueType', 'String', 'IsSecret', false),
            jsonb_build_object('Name', 'client_id', 'Value', c."ClientId", 'ValueType', 'String', 'IsSecret', false),
            jsonb_build_object('Name', 'client_secret', 'Value', c."ClientSecret", 'ValueType', 'String', 'IsSecret', true)
        ),
        'BusinessCodePath', 'code',
        'ExpectedBusinessCode', '200',
        'MessagePath', 'message',
        'TokenPath', 'data.token',
        'ExpiryPath', 'data.expiresIn',
        'ExpiryMode', 'RelativeSeconds',
        'RefreshAdvanceSeconds', 60,
        'HeaderName', 'Authorization',
        'HeaderScheme', 'Bearer'
    ),
    jsonb_build_object(
        'EndpointTemplate', c."QueryEndpoint",
        'ParameterMode', 'ConditionArray',
        'FixedParameters', jsonb_build_array(
            jsonb_build_object('Name', 'sysCode', 'Value', c."SysCode", 'ValueType', 'String', 'IsSecret', false)
        ),
        'InterfaceCodeField', 'serverCode',
        'ConditionArray', jsonb_build_object(
            'ArrayField', 'condition',
            'ColumnField', 'column',
            'OperatorField', 'type',
            'ValueField', 'value',
            'Operators', '[
                {"Value":"eq","DisplayName":"等于","RequiresValue":true},
                {"Value":"ne","DisplayName":"不等于","RequiresValue":true},
                {"Value":"lt","DisplayName":"小于","RequiresValue":true},
                {"Value":"le","DisplayName":"小于等于","RequiresValue":true},
                {"Value":"gt","DisplayName":"大于","RequiresValue":true},
                {"Value":"ge","DisplayName":"大于等于","RequiresValue":true},
                {"Value":"likeright","DisplayName":"前缀匹配","RequiresValue":true},
                {"Value":"in","DisplayName":"包含","RequiresValue":true},
                {"Value":"notin","DisplayName":"不包含","RequiresValue":true},
                {"Value":"isnull","DisplayName":"为空","RequiresValue":false},
                {"Value":"isnotnull","DisplayName":"不为空","RequiresValue":false}
            ]'::jsonb,
            'SingleValueOperator', 'eq',
            'MultiValueOperator', 'in',
            'StartTimeOperator', 'ge',
            'EndTimeOperator', 'le'
        ),
        'PaginationEnabled', true,
        'PageNumberField', 'pageNo',
        'PageSizeField', 'pageSize',
        'PageSize', c."PageSize",
        'MaxResultSizeField', 'maxResultSize',
        'MaxResultSize', c."MaxResultSize",
        'StartTimeField', '',
        'EndTimeField', '',
        'DateTimeFormat', 'yyyy-MM-dd HH:mm:ss',
        'TimeRangeUsesPagination', true
    ),
    jsonb_build_object(
        'BusinessCodePath', '', 'ExpectedBusinessCode', '', 'MessagePath', '',
        'DataPath', '', 'HasMorePath', '', 'TotalPagesPath', ''
    ),
    c."RequestIntervalMilliseconds",
    true,
    c."DebugLogEnabled",
    true,
    c."UpdatedAt"
FROM cyyy.data_lake_configs c
WHERE NOT EXISTS (SELECT 1 FROM cyyy.api_platform_configs p WHERE p.name = '数据湖')
LIMIT 1;

INSERT INTO cyyy.api_platform_configs (
    name, base_url, auth_config, query_config, response_config,
    request_interval_milliseconds, ignore_ssl_errors, debug_log_enabled, enabled, updated_at)
SELECT
    '动态接口',
    c."BaseUrl",
    jsonb_build_object(
        'TokenEndpoint', c."TokenEndpoint",
        'RequestType', 'Json',
        'Parameters', jsonb_build_array(
            jsonb_build_object('Name', 'appKey', 'Value', c."AppKey", 'ValueType', 'String', 'IsSecret', false),
            jsonb_build_object('Name', 'appSecret', 'Value', c."AppSecret", 'ValueType', 'String', 'IsSecret', true)
        ),
        'BusinessCodePath', 'code',
        'ExpectedBusinessCode', '0',
        'MessagePath', 'message',
        'TokenPath', 'data.token',
        'ExpiryPath', 'data.expireAt',
        'ExpiryMode', 'UnixSeconds',
        'RefreshAdvanceSeconds', 60,
        'HeaderName', 'Authorization',
        'HeaderScheme', 'Bearer'
    ),
    jsonb_build_object(
        'EndpointTemplate', c."QueryEndpointPrefix" || '/{path}',
        'ParameterMode', 'DirectProperties',
        'FixedParameters', '[]'::jsonb,
        'InterfaceCodeField', '',
        'ConditionArray', NULL,
        'PaginationEnabled', true,
        'PageNumberField', 'pageNum',
        'PageSizeField', 'pageSize',
        'PageSize', c."PageSize",
        'MaxResultSizeField', '',
        'MaxResultSize', NULL,
        'StartTimeField', 'startTime',
        'EndTimeField', 'endTime',
        'DateTimeFormat', 'yyyy-MM-dd HH:mm:ss',
        'TimeRangeUsesPagination', false
    ),
    jsonb_build_object(
        'BusinessCodePath', 'code',
        'ExpectedBusinessCode', '0',
        'MessagePath', 'message',
        'DataPath', 'data',
        'HasMorePath', 'pagination.hasMore',
        'TotalPagesPath', 'pagination.totalPages'
    ),
    c."RequestIntervalMilliseconds",
    false,
    false,
    true,
    c."UpdatedAt"
FROM cyyy.dynamic_api_configs c
WHERE NOT EXISTS (SELECT 1 FROM cyyy.api_platform_configs p WHERE p.name = '动态接口')
LIMIT 1;

INSERT INTO cyyy.api_interfaces (api_platform_id, code, name, relative_path)
SELECT p.id, d.server_code, d.name, ''
FROM cyyy.data_lake_interfaces d
JOIN cyyy.api_platform_configs p ON p.name = '数据湖'
ON CONFLICT (api_platform_id, code, relative_path) DO NOTHING;

INSERT INTO cyyy.api_interfaces (api_platform_id, code, name, relative_path)
SELECT DISTINCT p.id, x.code, x.name, x.relative_path
FROM (
    SELECT server_code AS code, COALESCE(NULLIF(display_name, ''), server_code) AS name,
           COALESCE(query_path, '') AS relative_path
    FROM cyyy.sync_task_interfaces
    WHERE LOWER(source_type) = 'dynamicapi'
    UNION
    SELECT server_code, COALESCE(NULLIF(name, ''), server_code), COALESCE(query_path, '')
    FROM cyyy.ingestion_sources
    WHERE LOWER(source_type) = 'dynamicapi'
) x
JOIN cyyy.api_platform_configs p ON p.name = '动态接口'
WHERE x.code <> ''
ON CONFLICT (api_platform_id, code, relative_path) DO NOTHING;

UPDATE cyyy.sync_task_interfaces t
SET api_interface_id = i.id,
    source_type = 'Api'
FROM cyyy.api_interfaces i
JOIN cyyy.api_platform_configs p ON p.id = i.api_platform_id
WHERE t.api_interface_id IS NULL
  AND LOWER(t.source_type) NOT IN ('database', 'sqlserver', 'oracle')
  AND i.code = t.server_code
  AND (
      (p.name = '数据湖' AND LOWER(t.source_type) <> 'dynamicapi' AND i.relative_path = '')
      OR
      (p.name = '动态接口' AND LOWER(t.source_type) = 'dynamicapi' AND i.relative_path = COALESCE(t.query_path, ''))
  );

UPDATE cyyy.ingestion_sources s
SET api_interface_id = i.id,
    source_type = 'Api'
FROM cyyy.api_interfaces i
JOIN cyyy.api_platform_configs p ON p.id = i.api_platform_id
WHERE s.api_interface_id IS NULL
  AND LOWER(s.source_type) NOT IN ('database', 'sqlserver', 'oracle')
  AND i.code = s.server_code
  AND (
      (p.name = '数据湖' AND LOWER(s.source_type) <> 'dynamicapi' AND i.relative_path = '')
      OR
      (p.name = '动态接口' AND LOWER(s.source_type) = 'dynamicapi' AND i.relative_path = COALESCE(s.query_path, ''))
  );

UPDATE cyyy.sync_task_interfaces
SET source_type = 'Api'
WHERE api_interface_id IS NOT NULL
  AND LOWER(source_type) NOT IN ('database', 'sqlserver', 'oracle');

UPDATE cyyy.ingestion_sources
SET source_type = 'Api'
WHERE api_interface_id IS NOT NULL
  AND LOWER(source_type) NOT IN ('database', 'sqlserver', 'oracle');

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_sync_task_interfaces_api_interface') THEN
        ALTER TABLE cyyy.sync_task_interfaces
            ADD CONSTRAINT fk_sync_task_interfaces_api_interface
            FOREIGN KEY (api_interface_id) REFERENCES cyyy.api_interfaces(id) ON DELETE RESTRICT;
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'fk_ingestion_sources_api_interface') THEN
        ALTER TABLE cyyy.ingestion_sources
            ADD CONSTRAINT fk_ingestion_sources_api_interface
            FOREIGN KEY (api_interface_id) REFERENCES cyyy.api_interfaces(id) ON DELETE RESTRICT;
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_sync_task_interfaces_api_interface_id
    ON cyyy.sync_task_interfaces (api_interface_id);

CREATE INDEX IF NOT EXISTS ix_ingestion_sources_api_interface_id
    ON cyyy.ingestion_sources (api_interface_id);

COMMENT ON TABLE cyyy.api_platform_configs IS '配置驱动的通用 API 平台';
COMMENT ON TABLE cyyy.api_interfaces IS '通用 API 接口目录';
COMMENT ON COLUMN cyyy.sync_task_interfaces.query_mappings IS '根接口请求字段与触发记录字段映射 JSON';
COMMENT ON COLUMN cyyy.ingestion_sources.query_mappings IS '按对象补录时的 API 请求字段映射 JSON';
COMMENT ON COLUMN cyyy.sync_task_interfaces.source_type IS '查询来源类型：Api / Database';
COMMENT ON COLUMN cyyy.ingestion_sources.source_type IS '采集来源类型：Api / Database';
