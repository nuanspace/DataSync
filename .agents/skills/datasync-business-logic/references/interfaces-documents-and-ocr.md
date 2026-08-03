# 医院接口、文档、文件与 OCR

## 项目文档

医院接口文档由 `ProjectDocumentService` 和对应页面/API 管理，用于配置和实施参考。文档说明的是外部契约，是否已完整实现必须再核对当前接口配置、Controller 和处理器。

典型契约：

- 世纪坛数据服务：REST/JSON，先获取 Token，再以 `serverCode`、条件、排序和分页查询；CYYY 的 `DataLakeClient`、数据湖配置和 `dl_*` 数据池承担采集。
- 嘉和生命体征：SOAP 1.1，操作 `VitaInterface`，`INPUTPARA` 内为 XML；建议转为内部 JSON 后使用题目/子卡回写和待身份绑定。
- 嘉和移动医护评估单：SOAP 1.1，操作 `MOBILEASSESSMENT`，包含患者身份、评估时间、总分、风险和循环评估项；与生命体征可共享服务代码但使用不同操作和 SOAPAction。
- 世纪坛无纸化：涉及 PDF、CA、归档标记、报告数量和查询。现有文档管理和 ESB 能力不代表所有归档动作均已实现，新增前必须定位具体适配器。

## OCR 处理

`DataSync.Common` 提供 `IOcrConversionService` 和标准 OCR 结果模型；输入支持文件路径、URL 和 Base64 PDF，结果包含全文、页面、带坐标文本块、抽取字段和元数据。

运行链路：

1. Linux 容器通过 `pdftoppm` 渲染 PDF，再由 `tesseract` 识别；运行时不下载训练数据。
2. LHYY 仅在接口配置为自定义处理器且名称匹配 `OcrMessageProcessor` 时执行 OCR。
3. OCR 结果挂到原消息的 `Ocr` 节点，再复用 `GenericMessageProcessor` 和既有字段映射。
4. CYYY 主动采集 PDF 时只负责传递路径、URL 或 Base64；OCR 与 ntcare 写入仍在 LHYY。

## 文件和网络安全边界

- 文件路径必须在配置的 `allowed_file_roots` 内；既做字面预检，也解析已存在路径的 symlink/junction 最终目标。
- URL 必须为 `http/https` 且命中允许主机；每次请求和重定向都重新校验目标 IP。未配置 CIDR 时拒绝 loopback、私网、link-local、保留和多播地址。
- 禁用自动重定向并限制跳转次数；不得通过 30x 绕过主机/IP 白名单。
- Base64 在分配完整字节数组前按编码长度估算大小。
- 外部命令使用参数列表传参；超时时终止进程树，并限制终止后的等待时间。
- OCR 输出路径必须在 `AllowedOutputRoots` 中，文件名需包含消息身份、时间和随机后缀，避免并发覆盖。
- OCR 顶层输入要求 JSON 对象；不把顶层数组失败悄悄转成成功。
- 原始报告、OCR 文本和患者标识不得进入知识文档或普通日志。

## 归档与附件

- PDF、DOC、DICOM、CA 和归档状态属于外部协议的一部分；明确传输方向、大小限制、校验、幂等、存储生命周期和失败重试后再实现。
- 附件的可读 URL、容器挂载和物理目录必须作为同一链路验证，不能只验证数据库字段存在。
- FollowUp 医院包附件另受包清单、checksum、医院范围、备份和恢复约束，参见 `followup-hospital-sync.md`。

## 变更检查

外部接口字段、SOAPAction、文件来源、白名单、OCR 输出结构或归档状态改变时更新本文件。现场地址、凭据和患者样本只放安全配置或本地忽略证据，不写入知识库。
