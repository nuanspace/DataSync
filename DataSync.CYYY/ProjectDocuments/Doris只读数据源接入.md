# Doris 只读数据源接入

## 适用范围

用于通过 Doris FE 的 MySQL 协议读取医院批准的视图。应用只执行 `SELECT` 或 `WITH` 查询；源账号仍必须在数据库侧授予只读权限，应用校验不能替代权限控制。

## 配置步骤

1. 在“数据库资源”新增类型 `Doris`，主机填写 `host:port`（未写端口时使用 `9030`），数据库填写目标 catalog/database。
2. 凭据通过现场部署配置或受控密钥系统录入，不写入仓库的 `appsettings.json`。
3. 在采集源选择该数据库资源，主查询使用参数 `@from`、`@to`，例如：

   ```sql
   SELECT *
   FROM cdm_for_fuchanke.VIEW_VISITS_VISIT_INFO
   WHERE genesis_create_time >= @from
     AND genesis_create_time <= @to
   ```

4. 联合主键按视图配置，例如患者就诊范围使用 `PATIENT_SN,VISIT_SN`；报告及明细优先使用院方业务流水号。
5. 增量采集建议配置 2880 分钟回看窗口。没有可靠更新时间的视图应依靠业务键和内容变化重新入队，不得仅取最大时间水位。

## 安全与现场验收

- 用源账号执行一次写权限验证，确认 `INSERT`、`UPDATE`、`DELETE` 均被数据库拒绝。
- 查询模板不得包含分号、DML、DDL、`INTO OUTFILE` 或动态执行语句。
- 日志不得记录密码、证件号、患者姓名或完整原始报文。
- 首次同步先禁用下游写入，只核对源记录数、业务键重复数和预计目标记录数。
- `genesis_del=1` 首期进入人工对账，不自动删除 NTCare 临床记录。
