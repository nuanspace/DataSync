# 现场 secret 文件

本目录的模板不包含真实凭据。两种模式都需要创建：

- `datasync_db_password`

仅 `fresh-cube` 模式需要创建 `cube_db_password`。`external-cube` 的目标库凭据只填写在受限权限的 LHYY 生产配置中，不创建包内 Cube secret。

应用运行过程中会由 LHYY“医院端统一初始化”页面创建或导入以下外置文件，无需再逐个手工生成：

- `${DATA_ROOT}/secrets/cyyy/cyyy_dmz_ed25519`
- `${DATA_ROOT}/secrets/cyyy/dmz_known_hosts`
- `${DATA_ROOT}/secrets/cyyy/inner_device_token`
- `${DATA_ROOT}/secrets/lhyy/lhyy_package_private.pem`
- `${DATA_ROOT}/secrets/lhyy/cloud_signing_public.pem`

密码、Token 和私钥文件权限必须为 `0600`，不得进入发布包、Git、日志或普通工单。`package-release.sh` 只会复制本说明文件，不会复制本目录下的其他文件。

初始化顺序：LHYY 导出 `hospital-to-dmz.s7sync` → DMZ/云端完成两次交换 → LHYY 导入 `dmz-to-hospital.s7sync`。两个含 token 的回程包使用后必须从临时介质删除。
