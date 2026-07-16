-- 接口配置增加 SOAP 1.1 WebService 开放参数
ALTER TABLE IF EXISTS lhyy.esb_interface_config
    ADD COLUMN IF NOT EXISTS soap_enabled BOOLEAN NOT NULL DEFAULT FALSE;

ALTER TABLE IF EXISTS lhyy.esb_interface_config
    ADD COLUMN IF NOT EXISTS soap_service_code VARCHAR(100);

ALTER TABLE IF EXISTS lhyy.esb_interface_config
    ADD COLUMN IF NOT EXISTS soap_operation VARCHAR(100);

ALTER TABLE IF EXISTS lhyy.esb_interface_config
    ADD COLUMN IF NOT EXISTS soap_action VARCHAR(500);

CREATE INDEX IF NOT EXISTS ix_esb_interface_config_soap_service_code
    ON lhyy.esb_interface_config (soap_service_code);

CREATE UNIQUE INDEX IF NOT EXISTS ux_esb_interface_config_soap_operation
    ON lhyy.esb_interface_config (LOWER(soap_service_code), LOWER(soap_operation))
    WHERE soap_enabled = TRUE;

CREATE UNIQUE INDEX IF NOT EXISTS ux_esb_interface_config_soap_action
    ON lhyy.esb_interface_config (LOWER(soap_service_code), LOWER(soap_action))
    WHERE soap_enabled = TRUE;
