CREATE TABLE IF NOT EXISTS cyyy.dynamic_api_configs (
    "Id"                          SERIAL PRIMARY KEY,
    "BaseUrl"                     VARCHAR(500) NOT NULL,
    "TokenEndpoint"               VARCHAR(200) NOT NULL DEFAULT '/api/v1/auth/token',
    "QueryEndpointPrefix"         VARCHAR(300) NOT NULL DEFAULT '/api/dynamic/api/inpatient/query',
    "AppKey"                      VARCHAR(200) NOT NULL DEFAULT '',
    "AppSecret"                   VARCHAR(500) NOT NULL DEFAULT '',
    "PageSize"                    INT NOT NULL DEFAULT 100,
    "RequestIntervalMilliseconds" INT NOT NULL DEFAULT 200,
    "UpdatedAt"                   TIMESTAMP NOT NULL DEFAULT NOW()
);

ALTER TABLE cyyy.sync_task_interfaces
ADD COLUMN IF NOT EXISTS query_path TEXT;

ALTER TABLE cyyy.sync_task_interfaces
ADD COLUMN IF NOT EXISTS use_today_time_range BOOLEAN NOT NULL DEFAULT FALSE;

COMMENT ON TABLE cyyy.dynamic_api_configs IS '动态接口平台连接与认证配置';
COMMENT ON COLUMN cyyy.sync_task_interfaces.query_path IS '动态接口查询端点前缀后的路径部分';
COMMENT ON COLUMN cyyy.sync_task_interfaces.use_today_time_range IS '是否传入当天完整时间范围';
