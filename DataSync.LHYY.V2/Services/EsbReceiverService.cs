using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DataSync.LHYY.V2.Data;
using DataSync.LHYY.V2.Models.Dto;
using DataSync.LHYY.V2.Models.Entities;
using DataSync.LHYY.V2.Models.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Newtonsoft.Json.Linq;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// 接收消息，并按接口配置决定是入队还是直处理
/// </summary>
public class EsbReceiverService
{
    private const string MultiTranCode = "MULTI";
    private const string MultiTranName = "多接口请求";

    private readonly IDbContextFactory<DataSyncDbContext> _contextFactory;
    private readonly IntegrationProjectService _integrationProjectService;
    private readonly InterfaceRecognitionService _recognitionService;
    private readonly IdempotentKeyService _idempotentKeyService;
    private readonly MessageExecutionService _messageExecutionService;
    private readonly ILogger<EsbReceiverService> _logger;

    public EsbReceiverService(
        IDbContextFactory<DataSyncDbContext> contextFactory,
        IntegrationProjectService integrationProjectService,
        InterfaceRecognitionService recognitionService,
        IdempotentKeyService idempotentKeyService,
        MessageExecutionService messageExecutionService,
        ILogger<EsbReceiverService> logger)
    {
        _contextFactory = contextFactory;
        _integrationProjectService = integrationProjectService;
        _recognitionService = recognitionService;
        _idempotentKeyService = idempotentKeyService;
        _messageExecutionService = messageExecutionService;
        _logger = logger;
    }

    public async Task<object> ProcessAsync(string rawJson, CancellationToken cancellationToken = default)
    {
        var currentProjectCode = await _integrationProjectService.GetCurrentProjectCodeAsync();
        var hasParsedRoot = MessageJsonHelper.TryParseToken(rawJson, out var root, out var parseError);
        var message = BuildRequestLog(currentProjectCode, rawJson, hasParsedRoot ? root : null);

        await using (var db = await _contextFactory.CreateDbContextAsync(cancellationToken))
        {
            db.EsbMessages.Add(message);
            await db.SaveChangesAsync(cancellationToken);
        }

        if (!hasParsedRoot)
        {
            var errorMessage = string.IsNullOrWhiteSpace(rawJson)
                ? "请求体为空"
                : parseError ?? "JSON 格式错误";
            await FinalizeInvalidMessageAsync(message.Id, errorMessage, incrementRetry: false, cancellationToken);
            return string.IsNullOrWhiteSpace(rawJson)
                ? BuildEsbResponse("200.2", errorMessage, null, null)
                : BuildDefaultJsonResponse(1, errorMessage);
        }

        var result = await ProcessRequestMessageAsync(
            message.Id,
            currentProjectCode,
            rawJson,
            root,
            allowQueue: true,
            incrementRetryOnFailure: false,
            cancellationToken);

        return result.Response ?? BuildDefaultJsonResponse(1, "系统内部错误");
    }

    public async Task ProcessQueuedMessageAsync(long messageId, CancellationToken cancellationToken = default)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var message = await db.EsbMessages.FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);
        if (message == null)
            return;

        if (!MessageJsonHelper.TryParseToken(message.RawJson, out var root, out var parseError))
        {
            await FinalizeInvalidMessageAsync(
                message.Id,
                parseError ?? "JSON 格式错误",
                incrementRetry: true,
                cancellationToken);
            return;
        }

        await ProcessRequestMessageAsync(
            message.Id,
            message.IntegrationProjectCode ?? await _integrationProjectService.GetCurrentProjectCodeAsync(),
            message.RawJson,
            root,
            allowQueue: false,
            incrementRetryOnFailure: true,
            cancellationToken);
    }

    private async Task<HandleMessageResult> ProcessRequestMessageAsync(
        long messageId,
        string currentProjectCode,
        string rawJson,
        JToken root,
        bool allowQueue,
        bool incrementRetryOnFailure,
        CancellationToken cancellationToken)
    {
        var analysis = await AnalyzeRequestAsync(root, rawJson, currentProjectCode);

        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var message = await db.EsbMessages.FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);
        if (message == null)
            return new HandleMessageResult();

        ApplyMessageMatchSummary(message, analysis);
        ApplyMessageQueryFields(message, analysis);

        if (!analysis.HasAnyMatch)
        {
            message.Status = MessageStatus.Unmatched;
            message.ErrorMessage = GetUnmatchedSummaryMessage(analysis);
            message.ProcessedAt = DateTime.Now;
            message.ProcessingStartedAt = null;
            await db.SaveChangesAsync(cancellationToken);

            await WriteRequestProcessLogAsync(
                message.Id,
                message.IntegrationProjectCode,
                MessageStatus.Unmatched,
                CreateUnmatchedLogDetail(analysis),
                0,
                cancellationToken);

            return new HandleMessageResult
            {
                Response = BuildUnmatchedResponse(analysis)
            };
        }

        if (allowQueue && analysis.ShouldQueue)
        {
            message.Status = MessageStatus.Pending;
            message.ErrorMessage = null;
            message.ProcessedAt = null;
            message.ProcessingStartedAt = null;
            await db.SaveChangesAsync(cancellationToken);

            return new HandleMessageResult
            {
                Response = BuildQueuedResponse(analysis)
            };
        }

        if (allowQueue)
        {
            message.Status = MessageStatus.Processing;
            message.ErrorMessage = null;
            message.ProcessingStartedAt = DateTime.Now;
            await db.SaveChangesAsync(cancellationToken);
        }

        var stopwatch = Stopwatch.StartNew();
        var summary = await ExecuteRequestAnalysisAsync(analysis, message.Id, currentProjectCode, cancellationToken);
        stopwatch.Stop();

        var finalStatus = ResolveFinalStatus(summary);
        message.Status = finalStatus;
        message.ErrorMessage = BuildRequestErrorMessage(summary, finalStatus);
        message.PatientId = summary.PatientId;
        message.EventId = summary.EventId;
        message.ProcessedAt = DateTime.Now;
        message.ProcessingStartedAt = null;
        if (incrementRetryOnFailure && finalStatus == MessageStatus.Failed)
            message.RetryCount++;

        await db.SaveChangesAsync(cancellationToken);

        await WriteRequestProcessLogAsync(
            message.Id,
            message.IntegrationProjectCode,
            finalStatus,
            CreateProcessLogDetail(summary),
            (int)stopwatch.ElapsedMilliseconds,
            cancellationToken);

        return new HandleMessageResult
        {
            Response = allowQueue
                ? BuildExecutionResponse(summary, analysis.ResponsePreference)
                : null
        };
    }

    private async Task FinalizeInvalidMessageAsync(
        long messageId,
        string errorMessage,
        bool incrementRetry,
        CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var message = await db.EsbMessages.FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);
        if (message == null)
            return;

        message.Status = MessageStatus.Failed;
        message.ErrorMessage = errorMessage;
        message.ProcessedAt = DateTime.Now;
        message.ProcessingStartedAt = null;
        if (incrementRetry)
            message.RetryCount++;
        await db.SaveChangesAsync(cancellationToken);

        await WriteRequestProcessLogAsync(
            message.Id,
            message.IntegrationProjectCode,
            MessageStatus.Failed,
            CreateFailureLogDetail(errorMessage),
            0,
            cancellationToken);
    }

    private async Task<RequestAnalysisResult> AnalyzeRequestAsync(
        JToken root,
        string rawJson,
        string currentProjectCode)
    {
        var analysis = new RequestAnalysisResult
        {
            ProjectCode = currentProjectCode,
            LegacyTranCode = MessageJsonHelper.TryGetLegacyTranCode(root),
            LegacyMessageId = MessageJsonHelper.TryGetLegacyMessageId(root),
            IsBatch = root is JArray
        };

        if (root is JArray rootArray)
        {
            for (var index = 0; index < rootArray.Count; index++)
            {
                var item = rootArray[index];
                if (item is not JObject && item is not JArray)
                {
                    analysis.Items.Add(new AnalyzedRequestItem
                    {
                        RecordIndex = index.ToString(),
                        UnmatchedReason = "顶层数组元素必须是对象或数组"
                    });
                    continue;
                }

                var (payload, itemRawJson, matches) = await ResolveTopLevelArrayItemForProjectAsync(item, currentProjectCode);
                analysis.Items.Add(new AnalyzedRequestItem
                {
                    RecordIndex = index.ToString(),
                    Payload = payload,
                    RawJson = itemRawJson,
                    Matches = matches,
                    UnmatchedReason = matches.Count == 0 ? "未匹配到任何接口配置" : null
                });

                if (matches.Count > 0 && analysis.ResponsePreference == null)
                    analysis.ResponsePreference = CreateResponsePreference(payload, matches[0]);
            }
        }
        else
        {
            var matches = await _recognitionService.ResolveAsync(root, currentProjectCode);
            analysis.Items.Add(new AnalyzedRequestItem
            {
                Payload = root,
                RawJson = rawJson,
                Matches = matches,
                UnmatchedReason = matches.Count == 0 ? "未匹配到任何接口配置" : null
            });

            if (matches.Count > 0)
                analysis.ResponsePreference = CreateResponsePreference(root, matches[0]);
        }

        analysis.ShouldQueue = analysis.Items.Any(i =>
            i.Matches.Any(m => m.IsLegacyEsb || m.Config.ReceiveMode == ReceiveMode.PersistAndAsync));

        return analysis;
    }

    private async Task<(JToken Payload, string RawJson, List<InterfaceRecognitionResult> Matches)> ResolveTopLevelArrayItemForProjectAsync(
        JToken item,
        string currentProjectCode)
    {
        var directPayload = item.DeepClone();
        var directRawJson = directPayload.ToString(Newtonsoft.Json.Formatting.None);
        var directMatches = await _recognitionService.ResolveAsync(directPayload, currentProjectCode);
        if (directMatches.Count > 0 || item is JArray)
            return (directPayload, directRawJson, directMatches);

        var wrappedPayload = new JArray(item.DeepClone());
        var wrappedRawJson = wrappedPayload.ToString(Newtonsoft.Json.Formatting.None);
        var wrappedMatches = await _recognitionService.ResolveAsync(wrappedPayload, currentProjectCode);
        return (wrappedPayload, wrappedRawJson, wrappedMatches);
    }

    private async Task<BatchSummary> ExecuteRequestAnalysisAsync(
        RequestAnalysisResult analysis,
        long requestMessageId,
        string currentProjectCode,
        CancellationToken cancellationToken)
    {
        var summary = new BatchSummary
        {
            ProjectCode = currentProjectCode,
            IsBatch = analysis.IsBatch
        };

        foreach (var item in analysis.Items)
        {
            if (!string.IsNullOrWhiteSpace(item.UnmatchedReason))
            {
                RecordUnmatched(summary, item.RecordIndex, null, item.UnmatchedReason);
                continue;
            }

            if (item.Payload == null)
            {
                RecordFailure(summary, item.RecordIndex, null, "请求项缺少有效载荷");
                continue;
            }

            var itemSummary = await ExecuteResolvedItemAsync(
                item.Payload,
                item.RawJson ?? item.Payload.ToString(Newtonsoft.Json.Formatting.None),
                item.Matches,
                currentProjectCode,
                requestMessageId,
                item.RecordIndex,
                cancellationToken);

            MergeBatchSummary(summary, itemSummary);
        }

        return summary;
    }

    private async Task<BatchSummary> ExecuteResolvedItemAsync(
        JToken root,
        string rawJson,
        List<InterfaceRecognitionResult> matches,
        string currentProjectCode,
        long requestMessageId,
        string? baseRecordIndex,
        CancellationToken cancellationToken)
    {
        var summary = new BatchSummary
        {
            ProjectCode = currentProjectCode
        };

        foreach (var match in matches)
        {
            summary.MatchedTranCodes.Add(match.Config.TranCode);

            if (!TryBuildPayloadSlices(root, rawJson, match.Config, baseRecordIndex, out var slices, out var error))
            {
                RecordFailure(summary, baseRecordIndex, match.Config.TranCode, error ?? "批量拆分失败");
                continue;
            }

            if (matches.Count > 1 || slices.Count > 1)
                summary.IsBatch = true;

            foreach (var slice in slices)
            {
                await ExecuteResolvedSliceAsync(
                    currentProjectCode,
                    match,
                    slice,
                    requestMessageId,
                    summary,
                    cancellationToken);
            }
        }

        return summary;
    }

    private async Task ExecuteResolvedSliceAsync(
        string currentProjectCode,
        InterfaceRecognitionResult match,
        PayloadSlice slice,
        long requestMessageId,
        BatchSummary summary,
        CancellationToken cancellationToken)
    {
        var config = match.Config;

        try
        {
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var sourceMessageId = _idempotentKeyService.ResolveSourceMessageId(slice.Payload, config);
            var idempotentKey = await _idempotentKeyService.BuildIdempotentKeyAsync(slice.Payload, config);
            await using var idempotencyLock = await BeginIdempotencyLockAsync(
                db,
                currentProjectCode,
                config.TranCode,
                sourceMessageId,
                idempotentKey,
                cancellationToken);

            if (await MessageReceiptService.ExistsAsync(
                    db,
                    currentProjectCode,
                    config.TranCode,
                    sourceMessageId,
                    idempotentKey,
                    requestMessageId,
                    cancellationToken))
            {
                await CommitIdempotencyLockAsync(idempotencyLock, cancellationToken);
                summary.Duplicated++;
                RecordItem(summary, slice.RecordIndex, config.TranCode, MessageStatus.Success, "重复消息，已跳过");
                return;
            }

            var message = BuildMessage(currentProjectCode, slice.RawJson, slice.Payload, match, sourceMessageId, idempotentKey);
            var result = await _messageExecutionService.ExecuteAsync(message, config);
            CaptureRequestSteps(summary, result);

            if (result.OverrideStatus == MessageStatus.Pending)
            {
                await CommitIdempotencyLockAsync(idempotencyLock, cancellationToken);
                RecordFailure(summary, slice.RecordIndex, config.TranCode, "直处理模式不支持 Pending 策略", result.Steps);
                return;
            }

            if (result.IsSuccess || result.IsFiltered)
            {
                db.EsbMessageReceipts.Add(new EsbMessageReceipt
                {
                    IntegrationProjectCode = currentProjectCode,
                    TranCode = config.TranCode,
                    SourceMessageId = sourceMessageId,
                    IdempotentKey = idempotentKey,
                    CreatedAt = DateTime.Now,
                });

                await db.SaveChangesAsync(cancellationToken);
                await CommitIdempotencyLockAsync(idempotencyLock, cancellationToken);

                if (result.IsSuccess)
                {
                    summary.Processed++;
                    summary.PatientId ??= result.PatientId;
                    summary.EventId ??= result.EventId;
                    RecordItem(summary, slice.RecordIndex, config.TranCode, MessageStatus.Success, result.Message, result.Steps);
                }
                else
                {
                    summary.Filtered++;
                    RecordItem(summary, slice.RecordIndex, config.TranCode, MessageStatus.Filtered, result.Message, result.Steps);
                }

                return;
            }

            await CommitIdempotencyLockAsync(idempotencyLock, cancellationToken);
            RecordFailure(summary, slice.RecordIndex, config.TranCode, result.Message ?? "处理失败", result.Steps);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "请求级消息保存回执失败: TranCode={TranCode}, RecordIndex={RecordIndex}", config.TranCode, slice.RecordIndex);
            RecordFailure(summary, slice.RecordIndex, config.TranCode, $"数据写入失败: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "请求级消息处理异常: TranCode={TranCode}, RecordIndex={RecordIndex}", config.TranCode, slice.RecordIndex);
            RecordFailure(summary, slice.RecordIndex, config.TranCode, $"程序错误: {ex.Message}");
        }
    }

    private static async Task<IDbContextTransaction?> BeginIdempotencyLockAsync(
        DataSyncDbContext db,
        string? integrationProjectCode,
        string tranCode,
        string? sourceMessageId,
        string? idempotentKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceMessageId) && string.IsNullOrWhiteSpace(idempotentKey))
            return null;

        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var lockKey = AdvisoryLockKeyHelper.Build(
            "esb-receipt",
            integrationProjectCode,
            tranCode,
            string.IsNullOrWhiteSpace(sourceMessageId) ? null : "source:" + sourceMessageId,
            string.IsNullOrWhiteSpace(idempotentKey) ? null : "key:" + idempotentKey);
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock({0})",
            new object[] { lockKey },
            cancellationToken);
        return transaction;
    }

    private static async Task CommitIdempotencyLockAsync(
        IDbContextTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (transaction != null)
            await transaction.CommitAsync(cancellationToken);
    }

    private static void CaptureRequestSteps(BatchSummary summary, ProcessResult result)
    {
        if (result.Steps.Count == 0 || summary.Steps.Count > 0 || summary.ItemResults.Count > 0)
            return;

        foreach (var step in result.Steps)
            summary.Steps.Add(step);
    }

    private static MessageStatus ResolveFinalStatus(BatchSummary summary)
    {
        var hasHandled = summary.Processed > 0 || summary.Filtered > 0 || summary.Duplicated > 0;
        if (summary.Unmatched > 0 && !hasHandled && summary.Failed == 0)
            return MessageStatus.Unmatched;

        if (summary.Failed == 0 && summary.Unmatched == 0)
        {
            if (summary.Processed > 0 || summary.Duplicated > 0)
                return MessageStatus.Success;
            if (summary.Filtered > 0)
                return MessageStatus.Filtered;
        }

        if (hasHandled)
            return MessageStatus.PartialSuccess;

        return MessageStatus.Failed;
    }

    private static string? BuildRequestErrorMessage(BatchSummary summary, MessageStatus finalStatus)
    {
        return finalStatus switch
        {
            MessageStatus.Success => summary.Duplicated > 0 && summary.Processed == 0 && summary.Filtered == 0
                ? "重复消息，已跳过"
                : null,
            MessageStatus.Filtered => summary.Failures.FirstOrDefault()?.Message ?? "被过滤规则跳过",
            MessageStatus.Unmatched => summary.Failures.FirstOrDefault()?.Message ?? "未匹配到任何接口配置",
            MessageStatus.PartialSuccess => $"部分成功：成功 {summary.Processed}，过滤 {summary.Filtered}，重复 {summary.Duplicated}，失败 {summary.Failed}，未匹配 {summary.Unmatched}",
            _ => summary.Failures.FirstOrDefault()?.Message ?? "处理失败"
        };
    }

    private static string GetUnmatchedSummaryMessage(RequestAnalysisResult analysis)
    {
        return analysis.Items
                   .Select(i => i.UnmatchedReason)
                   .FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason))
               ?? "未匹配到任何接口配置";
    }

    private async Task WriteRequestProcessLogAsync(
        long messageId,
        string? integrationProjectCode,
        MessageStatus status,
        RequestProcessLogDetail detail,
        int elapsedMs,
        CancellationToken cancellationToken)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
        db.EsbProcessLogs.Add(new EsbProcessLog
        {
            MessageId = messageId,
            IntegrationProjectCode = integrationProjectCode,
            Step = status.ToDisplayText(),
            IsSuccess = status is MessageStatus.Success or MessageStatus.Filtered,
            Detail = JsonSerializer.Serialize(detail),
            ElapsedMs = elapsedMs,
            CreatedAt = DateTime.Now,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static RequestProcessLogDetail CreateFailureLogDetail(string message)
    {
        return new RequestProcessLogDetail
        {
            Failed = 1,
            Items =
            [
                new RequestProcessItemDetail
                {
                    Status = MessageStatus.Failed.ToDisplayText(),
                    Message = message
                }
            ]
        };
    }

    private static RequestProcessLogDetail CreateUnmatchedLogDetail(RequestAnalysisResult analysis)
    {
        return new RequestProcessLogDetail
        {
            IsBatch = analysis.IsBatch,
            Unmatched = analysis.Items.Count,
            Items = analysis.Items
                .Select(item => new RequestProcessItemDetail
                {
                    RecordIndex = item.RecordIndex,
                    Status = MessageStatus.Unmatched.ToDisplayText(),
                    Message = item.UnmatchedReason ?? "未匹配到任何接口配置"
                })
                .ToList()
        };
    }

    private static RequestProcessLogDetail CreateProcessLogDetail(BatchSummary summary)
    {
        return new RequestProcessLogDetail
        {
            IsBatch = summary.IsBatch,
            Queued = summary.Queued,
            Processed = summary.Processed,
            Filtered = summary.Filtered,
            Duplicated = summary.Duplicated,
            Failed = summary.Failed,
            Unmatched = summary.Unmatched,
            MatchedTranCodes = summary.MatchedTranCodes.ToList(),
            Items = summary.ItemResults.ToList(),
            Steps = summary.ItemResults.Count <= 1 ? summary.Steps.ToList() : []
        };
    }

    private static EsbMessage BuildRequestLog(string integrationProjectCode, string rawJson, JToken? root)
    {
        var message = new EsbMessage
        {
            MessageId = Guid.NewGuid().ToString("N"),
            IntegrationProjectCode = integrationProjectCode,
            SourceMessageId = root == null ? null : MessageJsonHelper.TryGetLegacyMessageId(root),
            TranCode = "",
            TranName = null,
            RawJson = NormalizeRawJsonForStorage(rawJson, root),
            BodyJson = root == null ? null : ExtractBodyJson(root),
            CreatedAt = DateTime.Now,
        };

        if (root == null)
            return message;

        var legacyHead = MessageJsonHelper.SafeSelectToken(root, "$.Request.Head") as JObject
            ?? MessageJsonHelper.SafeSelectToken(root, "[0].Request.Head") as JObject;
        if (legacyHead != null)
        {
            message.AppId = legacyHead["AppId"]?.ToString();
            message.OrgId = legacyHead["OrgId"]?.ToString();
            message.EsbTimestamp = legacyHead["Timestamp"]?.ToString();
        }

        return message;
    }

    private static string NormalizeRawJsonForStorage(string rawJson, JToken? root)
    {
        if (root != null)
            return rawJson;

        return JsonSerializer.Serialize(new
        {
            rawText = rawJson ?? string.Empty
        });
    }

    private static void ApplyMessageMatchSummary(EsbMessage message, RequestAnalysisResult analysis)
    {
        var matches = analysis.Items
            .SelectMany(i => i.Matches)
            .GroupBy(m => m.Config.TranCode, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        if (matches.Count == 1)
        {
            message.TranCode = matches[0].Config.TranCode;
            message.TranName = matches[0].Config.TranName ?? matches[0].Config.TranCode;
            return;
        }

        if (matches.Count > 1)
        {
            message.TranCode = MultiTranCode;
            message.TranName = MultiTranName;
            return;
        }

        message.TranCode = "";
        message.TranName = null;
    }

    private static void ApplyMessageQueryFields(EsbMessage message, RequestAnalysisResult analysis)
    {
        foreach (var item in analysis.Items)
        {
            if (item.Payload == null)
                continue;

            foreach (var match in item.Matches)
            {
                var config = match.Config;
                var mainContext = MessageJsonHelper.ResolveMainRecordContext(item.Payload, config.MainRecordArrayPath);

                if (string.IsNullOrWhiteSpace(message.Mrn))
                {
                    var mrn = MessageJsonHelper.ReadString(item.Payload, config.MrnSourcePath, mainContext);
                    if (!string.IsNullOrWhiteSpace(mrn))
                        message.Mrn = mrn;
                }

                if (!message.ResolvedEventTime.HasValue)
                    message.ResolvedEventTime = MessageJsonHelper.ReadDateTime(item.Payload, config.EventStartTimeSourcePath, mainContext);

                if (!string.IsNullOrWhiteSpace(message.Mrn) && message.ResolvedEventTime.HasValue)
                    return;
            }
        }
    }

    private async Task<object> ProcessAsyncLegacy(string rawJson)
    {
        if (!MessageJsonHelper.TryParseToken(rawJson, out var root, out var parseError))
            return BuildDefaultJsonResponse(1, parseError ?? "JSON 格式错误");

        if (root is JArray rootArray)
            return await ProcessTopLevelArrayAsync(rootArray);

        if (root is not JObject rootObject)
            return BuildDefaultJsonResponse(1, "仅支持 JSON 对象或对象数组");

        var currentProjectCode = await _integrationProjectService.GetCurrentProjectCodeAsync();
        var matches = await _recognitionService.ResolveAsync(rootObject);
        if (matches.Count == 0)
        {
            var legacyTranCode = MessageJsonHelper.TryGetLegacyTranCode(rootObject);
            var legacyMessageId = MessageJsonHelper.TryGetLegacyMessageId(rootObject);
            _logger.LogWarning(
                "接口未匹配: ProjectCode={ProjectCode}, ServerCode={ServerCode}, LegacyTranCode={LegacyTranCode}, LegacyMessageId={LegacyMessageId}, RootKeys={RootKeys}",
                currentProjectCode,
                MessageJsonHelper.ReadString(rootObject, "serverCode"),
                legacyTranCode,
                legacyMessageId,
                GetRootKeys(rootObject));
            if (!string.IsNullOrWhiteSpace(legacyTranCode))
                return BuildEsbResponse("200.2", "未找到已启用的接口配置", legacyTranCode, legacyMessageId);

            return BuildDefaultJsonResponse(1, "未匹配到任何接口配置");
        }

        if (matches.Any(m => !string.IsNullOrWhiteSpace(m.Config.MainRecordArrayPath)))
        {
            var summary = await ProcessExpandedMatchesAsync(rootObject, rawJson, matches, currentProjectCode);
            var responsePreference = CreateResponsePreference(rootObject, matches[0]);
            return BuildBatchResponse(summary, responsePreference);
        }

        return await ProcessSingleResolvedAsync(rootObject, rawJson, matches, currentProjectCode);
    }

    private async Task<object> ProcessTopLevelArrayAsync(JArray rootArray)
    {
        var currentProjectCode = await _integrationProjectService.GetCurrentProjectCodeAsync();
        var summary = new BatchSummary
        {
            ProjectCode = currentProjectCode,
            IsBatch = true
        };

        ResponsePreference? responsePreference = null;

        for (var index = 0; index < rootArray.Count; index++)
        {
            var item = rootArray[index];
            if (item is not JObject && item is not JArray)
            {
                RecordFailure(summary, index.ToString(), null, "顶层数组元素必须是对象或数组");
                continue;
            }

            var (payload, itemRawJson, matches) = await ResolveTopLevelArrayItemAsync(item);
            if (matches.Count == 0)
            {
                RecordFailure(summary, index.ToString(), null, "未匹配到任何接口配置");
                continue;
            }

            responsePreference ??= CreateResponsePreference(payload, matches[0]);
            var itemSummary = await ProcessExpandedMatchesAsync(payload, itemRawJson, matches, currentProjectCode, index.ToString());
            MergeBatchSummary(summary, itemSummary);
        }

        return BuildBatchResponse(summary, responsePreference);
    }

    private async Task<(JToken Payload, string RawJson, List<InterfaceRecognitionResult> Matches)> ResolveTopLevelArrayItemAsync(JToken item)
    {
        var directPayload = item.DeepClone();
        var directRawJson = directPayload.ToString(Newtonsoft.Json.Formatting.None);
        var directMatches = await _recognitionService.ResolveAsync(directPayload);
        if (directMatches.Count > 0 || item is JArray)
            return (directPayload, directRawJson, directMatches);

        var wrappedPayload = new JArray(item.DeepClone());
        var wrappedRawJson = wrappedPayload.ToString(Newtonsoft.Json.Formatting.None);
        var wrappedMatches = await _recognitionService.ResolveAsync(wrappedPayload);
        return (wrappedPayload, wrappedRawJson, wrappedMatches);
    }

    private async Task<object> ProcessSingleResolvedAsync(
        JToken root,
        string rawJson,
        List<InterfaceRecognitionResult> matches,
        string currentProjectCode)
    {
        var primary = matches[0];
        var queued = 0;
        var processed = 0;
        var filtered = 0;
        var duplicated = 0;
        var failures = new List<string>();

        try
        {
            await using var db = await _contextFactory.CreateDbContextAsync();

            foreach (var match in matches)
            {
                var config = match.Config;
                var sourceMessageId = _idempotentKeyService.ResolveSourceMessageId(root, config);
                var idempotentKey = await _idempotentKeyService.BuildIdempotentKeyAsync(root, config);

                if (await MessageReceiptService.ExistsAsync(db, currentProjectCode, config.TranCode, sourceMessageId, idempotentKey))
                {
                    duplicated++;
                    continue;
                }

                var message = BuildMessage(currentProjectCode, rawJson, root, match, sourceMessageId, idempotentKey);
                var shouldPersist = match.IsLegacyEsb || config.ReceiveMode == ReceiveMode.PersistAndAsync;
                if (shouldPersist)
                {
                    message.Status = MessageStatus.Pending;
                    db.EsbMessages.Add(message);
                    queued++;
                    continue;
                }

                var result = await _messageExecutionService.ExecuteAsync(message, config);
                if (result.OverrideStatus == MessageStatus.Pending)
                {
                    failures.Add($"{config.TranCode}: 直处理模式不支持 Pending 策略");
                    continue;
                }

                if (result.IsSuccess || result.IsFiltered)
                {
                    db.EsbMessageReceipts.Add(new EsbMessageReceipt
                    {
                        IntegrationProjectCode = currentProjectCode,
                        TranCode = config.TranCode,
                        SourceMessageId = sourceMessageId,
                        IdempotentKey = idempotentKey,
                        CreatedAt = DateTime.Now,
                    });

                    if (result.IsSuccess)
                        processed++;
                    else
                        filtered++;

                    continue;
                }

                failures.Add($"{config.TranCode}: {result.Message}");
            }

            if (queued > 0 || processed > 0 || filtered > 0)
                await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "接收消息保存失败");
            if (primary.IsLegacyEsb || primary.Config.ResponseMode == ApiResponseMode.Esb)
                return BuildEsbResponse("300.2", "数据写入失败", primary.Config.TranCode, MessageJsonHelper.TryGetLegacyMessageId(root));

            return BuildDefaultJsonResponse(1, "数据写入失败");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理接收消息时发生错误");
            if (primary.IsLegacyEsb || primary.Config.ResponseMode == ApiResponseMode.Esb)
                return BuildEsbResponse("300.1", $"程序错误: {ex.Message}", primary.Config.TranCode, MessageJsonHelper.TryGetLegacyMessageId(root));

            return BuildDefaultJsonResponse(1, $"程序错误: {ex.Message}");
        }

        if (failures.Count > 0)
        {
            var firstFailure = failures[0];
            if (primary.IsLegacyEsb || primary.Config.ResponseMode == ApiResponseMode.Esb)
                return BuildEsbResponse("300.1", firstFailure, primary.Config.TranCode, MessageJsonHelper.TryGetLegacyMessageId(root));

            return BuildDefaultJsonResponse(1, firstFailure, new
            {
                queued,
                processed,
                filtered,
                duplicated,
                projectCode = currentProjectCode,
                matchedTranCodes = matches.Select(m => m.Config.TranCode).ToArray(),
                failures
            });
        }

        if (primary.IsLegacyEsb || primary.Config.ResponseMode == ApiResponseMode.Esb)
        {
            return BuildEsbResponse(
                "100",
                duplicated > 0 && queued == 0 && processed == 0 && filtered == 0 ? "成功（重复消息）" : "成功",
                primary.Config.TranCode,
                MessageJsonHelper.TryGetLegacyMessageId(root));
        }

        return BuildDefaultJsonResponse(0, "success", new
        {
            queued,
            processed,
            filtered,
            duplicated,
            projectCode = currentProjectCode,
            matchedTranCodes = matches.Select(m => m.Config.TranCode).ToArray()
        });
    }

    private async Task<BatchSummary> ProcessExpandedMatchesAsync(
        JToken root,
        string rawJson,
        List<InterfaceRecognitionResult> matches,
        string currentProjectCode,
        string? baseRecordIndex = null)
    {
        var summary = new BatchSummary
        {
            ProjectCode = currentProjectCode
        };

        foreach (var match in matches)
        {
            summary.MatchedTranCodes.Add(match.Config.TranCode);

            if (!TryBuildPayloadSlices(root, rawJson, match.Config, baseRecordIndex, out var slices, out var error))
            {
                RecordFailure(summary, baseRecordIndex, match.Config.TranCode, error ?? "批量拆分失败");
                continue;
            }

            if (slices.Count > 1)
                summary.IsBatch = true;

            foreach (var slice in slices)
                await ProcessBatchSliceAsync(currentProjectCode, match, slice, summary);
        }

        return summary;
    }

    private async Task ProcessBatchSliceAsync(
        string currentProjectCode,
        InterfaceRecognitionResult match,
        PayloadSlice slice,
        BatchSummary summary)
    {
        var config = match.Config;

        try
        {
            await using var db = await _contextFactory.CreateDbContextAsync();
            var sourceMessageId = _idempotentKeyService.ResolveSourceMessageId(slice.Payload, config);
            var idempotentKey = await _idempotentKeyService.BuildIdempotentKeyAsync(slice.Payload, config);

            if (await MessageReceiptService.ExistsAsync(db, currentProjectCode, config.TranCode, sourceMessageId, idempotentKey))
            {
                summary.Duplicated++;
                return;
            }

            var message = BuildMessage(currentProjectCode, slice.RawJson, slice.Payload, match, sourceMessageId, idempotentKey);
            var shouldPersist = match.IsLegacyEsb || config.ReceiveMode == ReceiveMode.PersistAndAsync;
            if (shouldPersist)
            {
                message.Status = MessageStatus.Pending;
                db.EsbMessages.Add(message);
                await db.SaveChangesAsync();
                summary.Queued++;
                return;
            }

            var result = await _messageExecutionService.ExecuteAsync(message, config);
            if (result.OverrideStatus == MessageStatus.Pending)
            {
                RecordFailure(summary, slice.RecordIndex, config.TranCode, "直处理模式不支持 Pending 策略");
                return;
            }

            if (result.IsSuccess || result.IsFiltered)
            {
                db.EsbMessageReceipts.Add(new EsbMessageReceipt
                {
                    IntegrationProjectCode = currentProjectCode,
                    TranCode = config.TranCode,
                    SourceMessageId = sourceMessageId,
                    IdempotentKey = idempotentKey,
                    CreatedAt = DateTime.Now,
                });

                await db.SaveChangesAsync();
                if (result.IsSuccess)
                    summary.Processed++;
                else
                    summary.Filtered++;

                return;
            }

            RecordFailure(summary, slice.RecordIndex, config.TranCode, result.Message ?? "处理失败");
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "批量消息保存失败: TranCode={TranCode}, RecordIndex={RecordIndex}", config.TranCode, slice.RecordIndex);
            RecordFailure(summary, slice.RecordIndex, config.TranCode, $"数据写入失败: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "批量消息处理异常: TranCode={TranCode}, RecordIndex={RecordIndex}", config.TranCode, slice.RecordIndex);
            RecordFailure(summary, slice.RecordIndex, config.TranCode, $"程序错误: {ex.Message}");
        }
    }

    private static bool TryBuildPayloadSlices(
        JToken root,
        string rawJson,
        EsbInterfaceConfig config,
        string? baseRecordIndex,
        out List<PayloadSlice> slices,
        out string? error)
    {
        slices = [];
        error = null;

        if (string.IsNullOrWhiteSpace(config.MainRecordArrayPath))
        {
            slices.Add(new PayloadSlice(root, rawJson, baseRecordIndex));
            return true;
        }

        var arrayPath = SubCardPathHelper.NormalizeArrayContainerPath(config.MainRecordArrayPath);
        if (string.IsNullOrWhiteSpace(arrayPath))
        {
            error = "主记录数组路径为空";
            return false;
        }

        var arrayToken = MessageJsonHelper.SafeSelectToken(root, arrayPath);
        if (arrayToken is not JArray array)
        {
            error = $"主记录数组路径未命中数组: {arrayPath}";
            return false;
        }

        if (array.Count == 0)
        {
            error = $"主记录数组为空: {arrayPath}";
            return false;
        }

        for (var index = 0; index < array.Count; index++)
        {
            if (!TryBuildProjectedPayload(root, arrayPath, array[index], out var projectedPayload))
            {
                error = $"主记录数组拆分失败: {arrayPath}[{index}]";
                return false;
            }

            slices.Add(new PayloadSlice(
                projectedPayload,
                projectedPayload.ToString(Newtonsoft.Json.Formatting.None),
                BuildRecordIndex(baseRecordIndex, index)));
        }

        return true;
    }

    private static bool TryBuildProjectedPayload(JToken root, string arrayPath, JToken item, out JToken projectedPayload)
    {
        if (string.IsNullOrWhiteSpace(arrayPath) || SubCardPathHelper.IsRootContainerPath(arrayPath))
        {
            projectedPayload = new JArray(item.DeepClone());
            return true;
        }

        if (TryBuildSimpleProjectedPayload(root, arrayPath, item, out projectedPayload))
            return true;

        projectedPayload = root.DeepClone();
        var targetToken = MessageJsonHelper.SafeSelectToken(projectedPayload, arrayPath);
        if (targetToken == null)
            return false;

        targetToken.Replace(new JArray(item.DeepClone()));
        return true;
    }

    private static bool TryBuildSimpleProjectedPayload(
        JToken root,
        string arrayPath,
        JToken item,
        out JToken projectedPayload)
    {
        projectedPayload = null!;
        if (root is not JObject rootObject ||
            !arrayPath.StartsWith("$.", StringComparison.Ordinal) ||
            arrayPath.Contains('[', StringComparison.Ordinal) ||
            arrayPath.Contains(']', StringComparison.Ordinal))
        {
            return false;
        }

        var segments = arrayPath[2..].Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return false;

        if (!TryCloneObjectWithArrayItem(rootObject, segments, 0, item, out var clonedRoot))
            return false;

        projectedPayload = clonedRoot;
        return true;
    }

    private static bool TryCloneObjectWithArrayItem(
        JObject source,
        string[] segments,
        int depth,
        JToken item,
        out JObject cloned)
    {
        cloned = [];
        var targetName = segments[depth];
        var found = false;

        foreach (var property in source.Properties())
        {
            if (!string.Equals(property.Name, targetName, StringComparison.Ordinal))
            {
                cloned.Add(property.Name, property.Value.DeepClone());
                continue;
            }

            found = true;
            if (depth == segments.Length - 1)
            {
                if (property.Value is not JArray)
                    return false;

                cloned.Add(property.Name, new JArray(item.DeepClone()));
                continue;
            }

            if (property.Value is not JObject child ||
                !TryCloneObjectWithArrayItem(child, segments, depth + 1, item, out var clonedChild))
            {
                return false;
            }

            cloned.Add(property.Name, clonedChild);
        }

        return found;
    }

    private static string BuildRecordIndex(string? baseRecordIndex, int index)
    {
        if (string.IsNullOrWhiteSpace(baseRecordIndex))
            return index.ToString();

        return $"{baseRecordIndex}.{index}";
    }

    private static void MergeBatchSummary(BatchSummary target, BatchSummary source)
    {
        target.IsBatch |= source.IsBatch;
        target.Queued += source.Queued;
        target.Processed += source.Processed;
        target.Filtered += source.Filtered;
        target.Duplicated += source.Duplicated;
        target.Failed += source.Failed;
        target.Unmatched += source.Unmatched;

        foreach (var tranCode in source.MatchedTranCodes)
            target.MatchedTranCodes.Add(tranCode);

        target.Failures.AddRange(source.Failures);
        target.ItemResults.AddRange(source.ItemResults);
        if (target.Steps.Count == 0 && source.Steps.Count > 0 && source.ItemResults.Count <= 1)
            target.Steps.AddRange(source.Steps);

        target.PatientId ??= source.PatientId;
        target.EventId ??= source.EventId;
    }

    private static void RecordFailure(
        BatchSummary summary,
        string? recordIndex,
        string? tranCode,
        string message,
        List<ProcessStepInfo>? steps = null)
    {
        summary.Failed++;
        summary.Failures.Add(new BatchFailureDetail
        {
            RecordIndex = recordIndex,
            TranCode = tranCode,
            Message = message
        });
        RecordItem(summary, recordIndex, tranCode, MessageStatus.Failed, message, steps);
    }

    private static void RecordUnmatched(BatchSummary summary, string? recordIndex, string? tranCode, string message)
    {
        summary.Unmatched++;
        summary.Failures.Add(new BatchFailureDetail
        {
            RecordIndex = recordIndex,
            TranCode = tranCode,
            Message = message
        });
        RecordItem(summary, recordIndex, tranCode, MessageStatus.Unmatched, message);
    }

    private static void RecordItem(
        BatchSummary summary,
        string? recordIndex,
        string? tranCode,
        MessageStatus status,
        string? message,
        List<ProcessStepInfo>? steps = null)
    {
        summary.ItemResults.Add(new RequestProcessItemDetail
        {
            RecordIndex = recordIndex,
            TranCode = tranCode,
            Status = status.ToDisplayText(),
            Message = string.IsNullOrWhiteSpace(message) ? status.ToDisplayText() : message,
            Steps = steps?.ToList() ?? []
        });
    }

    private object BuildExecutionResponse(BatchSummary summary, ResponsePreference? responsePreference)
    {
        if (summary.IsBatch)
            return BuildRequestBatchResponse(summary, responsePreference);

        var finalStatus = ResolveFinalStatus(summary);
        var firstFailure = summary.Failures.FirstOrDefault()?.Message ?? "处理失败";
        var data = new
        {
            queued = summary.Queued,
            processed = summary.Processed,
            filtered = summary.Filtered,
            duplicated = summary.Duplicated,
            failed = summary.Failed,
            unmatched = summary.Unmatched,
            projectCode = summary.ProjectCode,
            matchedTranCodes = summary.MatchedTranCodes.ToArray()
        };

        if (responsePreference is { UseEsbResponse: true } preference)
        {
            if (finalStatus == MessageStatus.Failed)
                return BuildEsbResponse("300.1", firstFailure, preference.TranCode, preference.MessageId);

            return BuildEsbResponse(
                "100",
                summary.Duplicated > 0 && summary.Processed == 0 && summary.Filtered == 0 ? "成功（重复消息）" : "成功",
                preference.TranCode,
                preference.MessageId);
        }

        if (finalStatus == MessageStatus.Failed)
            return BuildDefaultJsonResponse(1, firstFailure, data);

        return BuildDefaultJsonResponse(0, "success", data);
    }

    private object BuildQueuedResponse(RequestAnalysisResult analysis)
    {
        var data = new
        {
            queued = 1,
            processed = 0,
            filtered = 0,
            duplicated = 0,
            failed = 0,
            unmatched = 0,
            projectCode = analysis.ProjectCode,
            matchedTranCodes = analysis.Items
                .SelectMany(i => i.Matches)
                .Select(m => m.Config.TranCode)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };

        if (analysis.ResponsePreference is { UseEsbResponse: true } preference)
            return BuildEsbResponse("100", "成功", preference.TranCode, preference.MessageId);

        return BuildDefaultJsonResponse(0, "success", data);
    }

    private object BuildUnmatchedResponse(RequestAnalysisResult analysis)
    {
        if (!string.IsNullOrWhiteSpace(analysis.LegacyTranCode))
            return BuildEsbResponse("200.2", "未找到已启用的接口配置", analysis.LegacyTranCode, analysis.LegacyMessageId);

        return BuildDefaultJsonResponse(1, "未匹配到任何接口配置");
    }

    private object BuildRequestBatchResponse(BatchSummary summary, ResponsePreference? responsePreference)
    {
        var hasHandled = summary.Queued > 0 || summary.Processed > 0 || summary.Filtered > 0 || summary.Duplicated > 0;
        var data = new
        {
            batch = true,
            queued = summary.Queued,
            processed = summary.Processed,
            filtered = summary.Filtered,
            duplicated = summary.Duplicated,
            failed = summary.Failed,
            unmatched = summary.Unmatched,
            projectCode = summary.ProjectCode,
            matchedTranCodes = summary.MatchedTranCodes.ToArray(),
            failures = summary.Failures
        };

        if (responsePreference is { UseEsbResponse: true } preference)
        {
            var ackCode = summary.Failed > 0 && !hasHandled ? "300.1" : "100";
            var ackMessage = (summary.Failed + summary.Unmatched) switch
            {
                0 => "成功",
                _ when hasHandled => "部分成功",
                _ => "全部失败"
            };

            return BuildEsbResponse(
                ackCode,
                ackMessage,
                preference.TranCode,
                preference.MessageId,
                JsonSerializer.Serialize(data));
        }

        var code = summary.Failed > 0 && !hasHandled ? 1 : 0;
        var message = (summary.Failed + summary.Unmatched) switch
        {
            0 => "success",
            _ when hasHandled => "partial_success",
            _ => "all_failed"
        };

        return BuildDefaultJsonResponse(code, message, data);
    }

    private object BuildBatchResponse(BatchSummary summary, ResponsePreference? responsePreference)
    {
        var hasHandled = summary.Queued > 0 || summary.Processed > 0 || summary.Filtered > 0 || summary.Duplicated > 0;
        var data = new
        {
            batch = true,
            queued = summary.Queued,
            processed = summary.Processed,
            filtered = summary.Filtered,
            duplicated = summary.Duplicated,
            failed = summary.Failed,
            projectCode = summary.ProjectCode,
            matchedTranCodes = summary.MatchedTranCodes.ToArray(),
            failures = summary.Failures
        };

        if (responsePreference is { UseEsbResponse: true } preference)
        {
            var ackCode = summary.Failed > 0 && !hasHandled ? "300.1" : "100";
            var ackMessage = summary.Failed switch
            {
                0 => "成功",
                _ when hasHandled => "部分成功",
                _ => "全部失败"
            };
            return BuildEsbResponse(
                ackCode,
                ackMessage,
                preference.TranCode,
                preference.MessageId,
                JsonSerializer.Serialize(data));
        }

        var code = summary.Failed > 0 && !hasHandled ? 1 : 0;
        var message = summary.Failed switch
        {
            0 => "success",
            _ when hasHandled => "partial_success",
            _ => "all_failed"
        };
        return BuildDefaultJsonResponse(code, message, data);
    }

    private static ResponsePreference CreateResponsePreference(JToken root, InterfaceRecognitionResult match)
    {
        return new ResponsePreference(
            match.IsLegacyEsb,
            match.Config.ResponseMode,
            match.Config.TranCode,
            MessageJsonHelper.TryGetLegacyMessageId(root));
    }

    private static EsbMessage BuildMessage(
        string integrationProjectCode,
        string rawJson,
        JToken root,
        InterfaceRecognitionResult match,
        string? sourceMessageId,
        string? idempotentKey)
    {
        var config = match.Config;
        var mainContext = MessageJsonHelper.ResolveMainRecordContext(root, config.MainRecordArrayPath);
        var eventTime = MessageJsonHelper.ReadDateTime(root, config.EventStartTimeSourcePath, mainContext);
        var message = new EsbMessage
        {
            MessageId = Guid.NewGuid().ToString("N"),
            IntegrationProjectCode = integrationProjectCode,
            SourceMessageId = sourceMessageId,
            TranCode = config.TranCode,
            TranName = config.TranName ?? config.TranCode,
            RawJson = rawJson,
            BodyJson = ExtractBodyJson(root),
            IdempotentKey = idempotentKey,
            Mrn = MessageJsonHelper.ReadString(root, config.MrnSourcePath, mainContext),
            VisitNo = MessageJsonHelper.ReadString(root, config.VisitNoSourcePath, mainContext),
            InpatientNo = MessageJsonHelper.ReadString(root, config.InpatientNoSourcePath, mainContext),
            ResolvedEventTime = eventTime,
            MatchedRuleGroup = match.MatchedGroup,
            CreatedAt = DateTime.Now,
        };

        var legacyHead = MessageJsonHelper.SafeSelectToken(root, "$.Request.Head") as JObject
            ?? MessageJsonHelper.SafeSelectToken(root, "[0].Request.Head") as JObject;
        if (legacyHead != null)
        {
            message.AppId = legacyHead["AppId"]?.ToString();
            message.OrgId = legacyHead["OrgId"]?.ToString();
            message.EsbTimestamp = legacyHead["Timestamp"]?.ToString();
        }

        return message;
    }

    private static string? ExtractBodyJson(JToken root)
    {
        var bodyJson = MessageJsonHelper.ExtractBodyJson(root);
        if (!string.IsNullOrWhiteSpace(bodyJson))
        {
            var contentEncoding = MessageJsonHelper.ReadString(root, "$.Request.Head.ContentEncoding")
                ?? MessageJsonHelper.ReadString(root, "[0].Request.Head.ContentEncoding");
            if (!string.IsNullOrWhiteSpace(bodyJson) &&
                string.Equals(contentEncoding, "gzip", StringComparison.OrdinalIgnoreCase))
            {
                return DecompressGzipBase64(bodyJson);
            }

            return bodyJson;
        }

        return root.ToString(Newtonsoft.Json.Formatting.None);
    }

    private static string? DecompressGzipBase64(string base64)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            using var input = new MemoryStream(bytes);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch
        {
            return base64;
        }
    }

    private static EsbResponseWrapper BuildEsbResponse(string ackCode, string ackMessage, string? tranCode, string? messageId, string body = "")
    {
        return new EsbResponseWrapper
        {
            Response = new EsbResponseInner
            {
                Head = new EsbResponseHead
                {
                    AckCode = ackCode,
                    AckMessage = ackMessage,
                    TranCode = tranCode,
                    MessageId = messageId,
                },
                Body = body
            },
        };
    }

    private static object BuildDefaultJsonResponse(int code, string message, object? data = null)
        => new
        {
            code,
            message,
            data
        };

    private static string GetRootKeys(JObject root) =>
        string.Join(",", root.Properties().Select(p => p.Name));

    private sealed class BatchSummary
    {
        public bool IsBatch { get; set; }
        public int Queued { get; set; }
        public int Processed { get; set; }
        public int Filtered { get; set; }
        public int Duplicated { get; set; }
        public int Failed { get; set; }
        public int Unmatched { get; set; }
        public string? ProjectCode { get; set; }
        public Guid? PatientId { get; set; }
        public Guid? EventId { get; set; }
        public HashSet<string> MatchedTranCodes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<BatchFailureDetail> Failures { get; } = [];
        public List<RequestProcessItemDetail> ItemResults { get; } = [];
        public List<ProcessStepInfo> Steps { get; } = [];
    }

    private sealed class BatchFailureDetail
    {
        public string? RecordIndex { get; init; }
        public string? TranCode { get; init; }
        public string Message { get; init; } = "";
    }

    private readonly record struct PayloadSlice(JToken Payload, string RawJson, string? RecordIndex);

    private sealed class HandleMessageResult
    {
        public object? Response { get; init; }
    }

    private sealed class RequestAnalysisResult
    {
        public bool IsBatch { get; set; }
        public bool ShouldQueue { get; set; }
        public string? ProjectCode { get; set; }
        public string? LegacyTranCode { get; set; }
        public string? LegacyMessageId { get; set; }
        public ResponsePreference? ResponsePreference { get; set; }
        public List<AnalyzedRequestItem> Items { get; } = [];
        public bool HasAnyMatch => Items.Any(i => i.Matches.Count > 0);
    }

    private sealed class AnalyzedRequestItem
    {
        public string? RecordIndex { get; init; }
        public JToken? Payload { get; init; }
        public string? RawJson { get; init; }
        public List<InterfaceRecognitionResult> Matches { get; init; } = [];
        public string? UnmatchedReason { get; init; }
    }

    private readonly record struct ResponsePreference(
        bool IsLegacyEsb,
        ApiResponseMode ResponseMode,
        string? TranCode,
        string? MessageId)
    {
        public bool UseEsbResponse => IsLegacyEsb || ResponseMode == ApiResponseMode.Esb;
    }
}
