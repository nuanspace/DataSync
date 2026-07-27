# 沈阳七院医院端部署包

本包支持两种部署模式，默认并推荐 `external-cube`：

- `external-cube`：部署 DataSyncDb、CYYY、LHYY，连接医院已有 NTCare/CubeDb；部署与升级脚本不会定义、启动、初始化、执行基础库恢复或升级 CubeDb；正常回传导入仍会按包内容写入目标库，并在导入前生成完整备份；
- `fresh-cube`：全新环境使用，额外部署并恢复包内 CubeDb。

共同部署以下三个容器：

- `s7-followup-datasync-db`：DataSync PostgreSQL 17 数据库；
- `s7-followup-cyyy`：采集及 DMZ 拉包服务；
- `s7-followup-lhyy`：Cube 导入服务。

仅 `fresh-cube` 增加 `s7-followup-cube-db`（PostgreSQL 17 + pgvector）。

本包不包含医院生产密码、密钥、患者数据或历史运行记录。两个 `*.dump` 都仅含结构；其中 Cube dump 和 Cube 镜像只供 `fresh-cube` 使用。

仓库中的本目录是离线部署包模板，不能直接执行 `install.sh`。正式交付前必须先准备四个 Docker 镜像、两个仅含结构的基础库 dump 和实施文档，再生成带完整性清单的发布目录：

```bash
bash package-release.sh \
  /tmp/s7-followup-release \
  /secure/dumps/datasync-base.dump \
  /secure/dumps/cube-base.dump \
  ./release-docs \
  ./.env.example
```

第五个参数是发布环境文件，至少需要定义 `RELEASE_VERSION`、`CYYY_IMAGE`、`LHYY_IMAGE`、`DATASYNC_DB_IMAGE` 和 `CUBE_DB_IMAGE`；这五项只认文件内定义，不会被调用进程继承的同名变量补齐。出包机还必须安装 `pg_restore`；脚本会读取两个 dump 的归档目录，发现 `TABLE DATA`、`SEQUENCE SET` 或大对象数据条目时直接拒绝出包。交付包同时保留两套 Compose、两种 LHYY 配置模板、四个独立镜像和两个独立 dump；`manifest/package-manifest.json` 说明模式，`manifest/FILES.csv` 逐项标明 `requiredFor`、用途和顺序，`manifest/SHA256SUMS.txt` 用于完整性校验。实施文档目录禁止符号链接和隐藏路径，只接受文档和图片文件。所有 Bash 脚本按 LF 行尾交付。只有生成后的目录才是下面安装步骤所指的“本包”。

实施人员按 `FILES.csv` 独立取用时，`external-cube` 可以不携带任何标为 `fresh-cube` 的 Cube 镜像、dump、配置和校验资产；`install.sh` 仍会校验所有当前模式必需文件。

## 安装顺序

先复制并编辑 `.env` 选择模式，再运行安装。不要只在当前 Shell 中临时赋值，`install.sh` 会读取包内 `.env`：

```bash
cp .env.example .env

# 推荐；连接现场已有目标库
sed -i 's/^DEPLOYMENT_MODE=.*/DEPLOYMENT_MODE=external-cube/' .env

# 或仅在全新空库场景使用
# sed -i 's/^DEPLOYMENT_MODE=.*/DEPLOYMENT_MODE=fresh-cube/' .env

sudo bash install.sh
# 填写两个生产配置和 DataSyncDb 密码；fresh-cube 还需填写 CubeDb 密码
```

`external-cube` 初始化 DataSyncDb 后直接启动：

```bash
docker compose --env-file .env -f docker-compose.yml up -d datasync-db
bash database/restore-fresh-databases.sh datasync
bash start.sh       # 启动前自动执行只读 cube-compat-check
bash status.sh
```

`cube-compat-check` 只在只读事务中核对连接、v2 默认启用的 23 张目标表、来源适配与 EDC 可见性维护表、必要字段、8 个 schema 的 `USAGE`、按导入策略实际需要的表权限、导入前完整 `pg_dump` 所需的全部业务 schema/table/sequence 读取权限，以及 form schema 下的 vector 扩展。`UseExistingById/RejectIfMissing` 要求 `SELECT`，`InsertIfMissing` 要求 `INSERT`，`Upsert` 要求 `SELECT/INSERT/UPDATE`（PostgreSQL 的 `ON CONFLICT DO UPDATE` 会读取 `EXCLUDED` 列）；当前导入流程不要求 `DELETE`。

启动检查还会读取目标库 `form.form_question.table_name`，逐一验证已引用的 `target.*` 动态表、`patient_event_id` 字段及 `INSERT/UPDATE` 权限。云端后续可能启用新的动态表，因此每个数据包在真正导入前仍会执行完整 schema/主键/字段类型检查；`external-cube` 模式还会按该包映射后的目标表和 `ImportPolicy` 再检查 schema `USAGE` 与最小表权限。新动态表缺失或权限不足时包会进入结构待处理状态，不会开始写库。实施人员应在首次包和云端表清单变更后先手工拉取并完成这道检查，再开启自动导入。

任一检查失败即拒绝启动或拒绝该包导入。`DEPLOYMENT_MODE` 会传入 LHYY，`external-cube` 模式下服务端和数据库升级页面均拒绝通过升级模块对 CubeDb 执行初始化、基础库恢复、比对同步、内置脚本或 SQL 文件，但正常数据包导入、导入前完整备份，以及对该备份的人工故障恢复不受影响。若结构不兼容，由目标库负责人先按正式发布流程完成升级，医院端部署包不代执行。

`fresh-cube` 的初始化步骤为：

```bash
docker compose --env-file .env -f docker-compose.fresh-cube.yml up -d datasync-db cube-db
bash database/restore-fresh-databases.sh datasync
bash database/restore-fresh-databases.sh cube
bash database/verify-fresh-databases.sh
bash start.sh
bash status.sh
```

全新部署的 Cube schema-only dump 必须已经包含 `20260722-cube-v2.sql` 的结构。`restore-fresh-databases.sh cube` 和 `verify-fresh-databases.sh` 会在 `external-cube` 模式主动拒绝执行。

首次部署完成后，先在 LHYY“医院端统一初始化”按 `hospital-to-dmz → dmz-to-cloud → cloud-to-dmz → dmz-to-hospital` 四包顺序完成三端信任。DMZ 运行期 SSH 授权与医院端七项材料均由页面即时应用，初始化阶段无需重启 DMZ、CYYY 或 LHYY。随后执行：关闭自动任务 → 检查服务和 v3/1.2.0（医院端拒绝旧 v2 包）→ 检查两库及来源映射 → 保存或核对 CYYY 医院来源 → CYYY 连接诊断 → 手工生成 Baseline → CYYY 拉取 → LHYY 备份并导入 → 重启 NTCare/刷新缓存并核对患者管理 → 检查 ACK → 验证 Incremental 和幂等 → 依次启用自动任务。详细步骤见包内 `docs/KEY-SEQUENCE.md`。

首次启动前必须保持两个业务开关为 `false`：

- `FollowUpPackageSync.Enabled=false`
- `FollowUpPackageImport.Enabled=false`

`.env` 中的 `CYYY_CONTAINER_UID` / `CYYY_CONTAINER_GID` 必须与 CYYY 镜像运行用户一致。LHYY 统一初始化以 root 生成 CYYY secret 后会把属主切换到该 UID/GID，确保 CYYY 能读取且文件仍保持 `0600`。

待 DMZ、密钥、known_hosts、数据库和三端链路验收完成后，再按实施手册依次启用。

详细参数、密钥交换、验收和回退步骤见包内 `docs/06-三端生产环境实施部署手册.md`。
