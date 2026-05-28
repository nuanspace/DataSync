# 项目持久规则

## 数据库变更 SQL 管理

- 所有数据库结构或数据变更，必须放到 `Scripts\yyyyMM\yyyyMMdd.sql`。
- 目录按月份管理，例如 `Scripts\202605`。
- 文件按具体日期命名，例如 `Scripts\202605\20260509.sql`。
- 同一天只维护一个 SQL 文件；当天后续数据库变更继续追加到该文件。
- 不要再拆成 `001_xxx.sql`、`002_xxx.sql` 这类多文件。

## 专项性能优化脚本

- 不纳入项目常规升级链的专项性能优化脚本，可以放在 `DatabaseUpgrades\` 下按主题单独管理。
- 专项脚本必须提供明确执行入口和验证步骤，例如 `upgrade_esb_messages_performance.bat` 调用 `message-archive upgrade`、`message-archive migrate`、`message-archive verify`。
- 专项脚本不应由数据库升级页面或常规 `database-upgrade` 命令自动扫描执行，避免和常规版本升级混用。
