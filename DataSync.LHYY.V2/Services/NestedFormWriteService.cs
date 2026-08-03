using Bio.Core.FormSetDC;
using Bio.Models;

namespace DataSync.LHYY.V2.Services;

/// <summary>
/// 使用表单服务递归写入带父子关系的子卡。
/// </summary>
public class NestedFormWriteService
{
    private readonly BioCoreIntegrationService _bioCore;
    private readonly ILogger<NestedFormWriteService> _logger;

    public NestedFormWriteService(
        BioCoreIntegrationService bioCore,
        ILogger<NestedFormWriteService> logger)
    {
        _bioCore = bioCore;
        _logger = logger;
    }

    public async Task<NestedFormWriteResult> WriteAsync(
        Guid patientEventId,
        IReadOnlyDictionary<Guid, form_question> questions,
        IEnumerable<QuestionValue> questionValues,
        IEnumerable<SubCardData> subCards)
    {
        var formsetService = await _bioCore.CreateFormsetServiceAsync(patientEventId);
        var result = new NestedFormWriteResult();

        foreach (var questionValue in questionValues)
        {
            if (!TryConvertValue(questionValue, questions, out var question, out var converted))
                continue;

            try
            {
                await formsetService.SetQuestionValue(question!, converted!);
                result.QuestionCount++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "设置 Question {QuestionId} 失败", questionValue.QuestionId);
            }
        }

        foreach (var subCard in subCards)
        {
            await WriteSubCardAsync(formsetService, subCard, null, questions, result);
        }

        return result;
    }

    private async Task WriteSubCardAsync(
        IFormsetService formsetService,
        SubCardData subCard,
        Guid? parentSubCardId,
        IReadOnlyDictionary<Guid, form_question> questions,
        NestedFormWriteResult result)
    {
        foreach (var row in subCard.Rows.Where(row => HasWritableContent(row, questions)))
        {
            var subCardId = await formsetService.AddSubCard(subCard.CardId, parentSubCardId);
            if (subCardId == Guid.Empty)
            {
                throw new InvalidOperationException($"创建 SubCard 失败: CardId={subCard.CardId}");
            }

            result.SubCardCount++;
            foreach (var questionValue in row.Values)
            {
                if (!TryConvertValue(questionValue, questions, out var question, out var converted))
                    continue;

                try
                {
                    await formsetService.SetQuestionValue(question!, converted!, subCardId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SubCard Question {QuestionId} 写入失败", questionValue.QuestionId);
                }
            }

            foreach (var child in row.Children)
            {
                await WriteSubCardAsync(formsetService, child, subCardId, questions, result);
            }
        }
    }

    private bool TryConvertValue(
        QuestionValue questionValue,
        IReadOnlyDictionary<Guid, form_question> questions,
        out form_question? question,
        out object? converted)
    {
        if (!questions.TryGetValue(questionValue.QuestionId, out question))
        {
            _logger.LogWarning("QuestionId {QuestionId} 不在当前 FormSet 中，已跳过写入", questionValue.QuestionId);
            converted = null;
            return false;
        }

        if (GenericMessageProcessor.ShouldSkipQuestionValue(question, questionValue, _logger))
        {
            converted = null;
            return false;
        }

        converted = GenericMessageProcessor.ConvertQuestionValue(
            question,
            questionValue.Value?.ToString(),
            _logger);
        return converted != null;
    }

    private static bool HasWritableContent(
        SubCardRowData row,
        IReadOnlyDictionary<Guid, form_question> questions)
    {
        var hasValue = row.Values.Any(value =>
            questions.TryGetValue(value.QuestionId, out var question)
            && !GenericMessageProcessor.ShouldSkipQuestionValue(question, value)
            && GenericMessageProcessor.ConvertQuestionValue(question, value.Value?.ToString()) != null);
        return hasValue || row.Children.Any(child => child.Rows.Any(childRow => HasWritableContent(childRow, questions)));
    }
}

public class NestedFormWriteResult
{
    public int QuestionCount { get; set; }
    public int SubCardCount { get; set; }
}
