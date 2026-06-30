using DataSync.LHYY.V2.Services;
using Xunit;

namespace DataSync.LHYY.V2.Tests;

public class DictServiceTests
{
    [Fact(DisplayName = "稳定型心绞痛只命中稳定型心绞痛")]
    public void TranslateEntries_StableAngina_ReturnsStableAngina()
    {
        var result = DictService.TranslateEntries("稳定型心绞痛", AnginaDict());

        Assert.Equal("稳定型心绞痛", result);
    }

    [Fact(DisplayName = "不稳定型心绞痛只命中不稳定型心绞痛")]
    public void TranslateEntries_UnstableAngina_ReturnsUnstableAngina()
    {
        var result = DictService.TranslateEntries("不稳定型心绞痛", AnginaDict());

        Assert.Equal("不稳定型心绞痛", result);
    }

    [Fact(DisplayName = "稳定型心绞痛和不稳定型心绞痛独立出现时都保留")]
    public void TranslateEntries_StableAndUnstableAngina_ReturnsBoth()
    {
        var result = DictService.TranslateEntries("稳定型心绞痛；不稳定型心绞痛", AnginaDict());

        Assert.Equal("稳定型心绞痛,不稳定型心绞痛", result);
    }

    [Fact(DisplayName = "非ST段抬高型心肌梗死只命中非ST段抬高型心肌梗死")]
    public void TranslateEntries_NonStemi_ReturnsNonStemi()
    {
        var result = DictService.TranslateEntries("非ST段抬高型心肌梗死", MyocardialInfarctionDict());

        Assert.Equal("非ST段抬高型心肌梗死", result);
    }

    [Fact(DisplayName = "多个诊断中保留独立诊断并剔除被覆盖短项")]
    public void TranslateEntries_MultipleDiagnoses_ReturnsIndependentMatches()
    {
        var dict = AnginaDict().Concat(MyocardialInfarctionDict());

        var result = DictService.TranslateEntries("稳定型心绞痛；非ST段抬高型心肌梗死", dict);

        Assert.Equal("稳定型心绞痛,非ST段抬高型心肌梗死", result);
    }

    private static (string SourceValue, string TargetValue)[] AnginaDict() =>
    [
        ("稳定型心绞痛", "稳定型心绞痛"),
        ("不稳定型心绞痛", "不稳定型心绞痛")
    ];

    private static (string SourceValue, string TargetValue)[] MyocardialInfarctionDict() =>
    [
        ("ST段抬高型心肌梗死", "ST段抬高型心肌梗死"),
        ("非ST段抬高型心肌梗死", "非ST段抬高型心肌梗死")
    ];
}
