# 沈阳七院医院端全新部署包

本包用于在医院内网 Linux 服务器上全新部署以下四个容器：

- `s7-followup-datasync-db`：DataSync PostgreSQL 17 数据库；
- `s7-followup-cube-db`：Cube PostgreSQL 17 + pgvector 数据库；
- `s7-followup-cyyy`：采集及 DMZ 拉包服务；
- `s7-followup-lhyy`：Cube 导入服务。

本包不包含医院生产密码、密钥、患者数据或历史运行记录。两个 `*.dump` 都是仅含结构的全新基础库。

仓库中的本目录是离线部署包模板，不能直接执行 `install.sh`。正式交付前必须先准备四个 Docker 镜像、两个仅含结构的基础库 dump 和实施文档，再生成带完整性清单的发布目录：

```bash
bash package-release.sh \
  /tmp/s7-followup-release \
  /secure/dumps/datasync-base.dump \
  /secure/dumps/cube-base.dump \
  ./release-docs \
  ./.env.example
```

第五个参数是发布环境文件，至少需要定义 `RELEASE_VERSION`、`CYYY_IMAGE`、`LHYY_IMAGE`、`DATASYNC_DB_IMAGE` 和 `CUBE_DB_IMAGE`；这五项只认文件内定义，不会被调用进程继承的同名变量补齐。出包机还必须安装 `pg_restore`；脚本会读取两个 dump 的归档目录，发现 `TABLE DATA`、`SEQUENCE SET` 或大对象数据条目时直接拒绝出包。脚本只把上述五项写入交付包的 `.env.example`，不会复制发布环境文件中的其他变量；配置目录只复制两个 `appsettings.Production.json.example`，`database` 只复制全新库恢复和校验脚本，`postgres-cube` 只复制 `Dockerfile`，`secrets` 只复制 README，避免本地已填写配置、凭据、旧 dump 或其他临时文件进入交付包。实施文档目录禁止符号链接和隐藏路径，只接受 `md`、`pdf`、`doc/docx`、`xls/xlsx`、`ppt/pptx`、`txt`、`png`、`jpg/jpeg`、`svg` 文件；脚本按物理路径判断输出目录不得位于实施文档目录内，符号链接别名也不能绕过。脚本会拒绝覆盖已有输出目录，验证所有镜像和输入制品，使用带序号的文件名导出镜像以避免不同镜像名清洗后发生覆盖，并生成、回验 `manifest/SHA256SUMS.txt`。所有 Bash 脚本按 LF 行尾交付。只有生成后的目录才是下面安装步骤所指的“本包”。

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

全新部署的 Cube schema-only dump 必须已经包含 `20260722-cube-v2.sql` 的结构。若现场使用既有 CubeDb 升级，不恢复全新基础 dump，则在启动 LHYY 前手工连接 CubeDb 执行包内 `database/20260722-cube-v2.sql`，再按实施文档核对 v2 表和字段。`verify-fresh-databases.sh` 只用于全新空库，已有运行历史的管理库不要使用。该 SQL 不要只对 DataSyncDb 执行。

首次部署完成后，先在 LHYY“医院端统一初始化”按 `hospital-to-dmz → dmz-to-cloud → cloud-to-dmz → dmz-to-hospital` 四包顺序完成三端信任，再执行：关闭自动任务 → 检查服务和 v2/1.1.0 → 检查两库及来源映射 → CYYY 连接诊断 → 手工生成 Baseline → CYYY 拉取 → LHYY 备份并导入 → 重启 NTCare/刷新缓存并核对患者管理 → 检查 ACK → 验证 Incremental 和幂等 → 依次启用自动任务。详细步骤见包内 `docs/KEY-SEQUENCE.md`。

首次启动前必须保持两个业务开关为 `false`：

- `FollowUpPackageSync.Enabled=false`
- `FollowUpPackageImport.Enabled=false`

`.env` 中的 `CYYY_CONTAINER_UID` / `CYYY_CONTAINER_GID` 必须与 CYYY 镜像运行用户一致。LHYY 统一初始化以 root 生成 CYYY secret 后会把属主切换到该 UID/GID，确保 CYYY 能读取且文件仍保持 `0600`。

待 DMZ、密钥、known_hosts、数据库和三端链路验收完成后，再按实施手册依次启用。

详细参数、密钥交换、验收和回退步骤见包内 `docs/06-三端生产环境实施部署手册.md`。
