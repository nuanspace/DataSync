# 沈阳七院医院端全新部署包

本包用于在医院内网 Linux 服务器上全新部署以下四个容器：

- `s7-followup-datasync-db`：DataSync PostgreSQL 17 数据库；
- `s7-followup-cube-db`：Cube PostgreSQL 17 + pgvector 数据库；
- `s7-followup-cyyy`：采集及 DMZ 拉包服务；
- `s7-followup-lhyy`：Cube 导入服务。

本包不包含医院生产密码、密钥、患者数据或历史运行记录。两个 `*.dump` 都是仅含结构的全新基础库。

## 安装顺序

```bash
sudo bash install.sh

# 填写 .env、config/*/appsettings.Production.json 和 secrets/*

docker compose up -d datasync-db cube-db
bash database/restore-fresh-databases.sh datasync
bash database/restore-fresh-databases.sh cube
bash database/verify-fresh-databases.sh

bash start.sh
bash status.sh
```

首次启动前必须保持两个业务开关为 `false`：

- `FollowUpPackageSync.Enabled=false`
- `FollowUpPackageImport.Enabled=false`

待 DMZ、密钥、known_hosts、数据库和三端链路验收完成后，再按实施手册依次启用。

详细参数、密钥交换、验收和回退步骤见包内 `docs/06-三端生产环境实施部署手册.md`。
