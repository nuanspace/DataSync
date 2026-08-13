using System.Text;
using DataSync.LHYY.V2.Models.Entities;
using DataSync.LHYY.V2.Models.Enums;
using DataSync.LHYY.V2.Models.Html;
using DataSync.LHYY.V2.Services;
using Xunit;

namespace DataSync.LHYY.V2.Tests;

public sealed class HtmlTextExtractionServiceTests
{
    [Fact]
    public void Extract_双重转义换行_提取章节和细字段()
    {
        const string source = "&lt;br&gt;主诉：胸部不适2天&lt;br&gt;现病史：活动后不适。&lt;br&gt;体格检查&lt;br&gt;体温(T) 36.5 ℃  脉搏(P) 67 次/分  呼吸(R) 16 次/分  血压(BP) 128/73 mmHg&lt;br&gt;初步诊断&lt;br&gt;1. 诊断甲&lt;br&gt;2. 诊断乙";
        var profile = CreateProfile();
        var rules = new List<HtmlExtractionRule>
        {
            new() { FieldCode = "主诉", SourceSection = "主诉", ExtractionType = HtmlExtractionType.Section },
            new() { FieldCode = "体温", SourceSection = "体格检查", SourceLabel = "体温(T)", ExtractionType = HtmlExtractionType.VitalSign },
            new() { FieldCode = "脉搏", SourceSection = "体格检查", SourceLabel = "脉搏(P)", ExtractionType = HtmlExtractionType.VitalSign },
            new() { FieldCode = "呼吸", SourceSection = "体格检查", SourceLabel = "呼吸(R)", ExtractionType = HtmlExtractionType.VitalSign },
            new() { FieldCode = "血压", SourceSection = "体格检查", SourceLabel = "血压(BP)", ExtractionType = HtmlExtractionType.VitalSign },
            new() { FieldCode = "初步诊断", SourceSection = "初步诊断", ExtractionType = HtmlExtractionType.DiagnosisList }
        };

        var result = new HtmlTextExtractionService().Extract(ToBase64(source, Encoding.UTF8), profile, rules);

        Assert.Equal("胸部不适2天", result.Sections["主诉"]);
        Assert.Equal("胸部不适2天", result.ExtractedFields["主诉"]);
        Assert.Equal("36.5 ℃", result.ExtractedFields["体温"]);
        Assert.Equal("67 次/分", result.ExtractedFields["脉搏"]);
        Assert.Equal("16 次/分", result.ExtractedFields["呼吸"]);
        Assert.Equal("128/73 mmHg", result.ExtractedFields["血压"]);
        Assert.Equal("诊断甲\n诊断乙", result.ExtractedFields["初步诊断"]);
        Assert.Empty(result.MissingRequiredFields);
    }

    [Fact]
    public void Extract_换行两侧空白_清理时不插入捕获组符号()
    {
        const string source = "   姓名：测试患者    职业：\t&lt;br&gt;   性别：男\t  &lt;br&gt;   主诉：胸闷";

        var result = new HtmlTextExtractionService().Extract(
            ToBase64(source, Encoding.UTF8),
            CreateProfile(),
            []);

        Assert.Equal("姓名：测试患者    职业：\n性别：男\n主诉：胸闷", result.CleanedText);
        Assert.DoesNotContain("$1", result.CleanedText, StringComparison.Ordinal);
    }

    [Fact]
    public void Extract_空格表格存在空白首列_保留相对缩进和列位置()
    {
        const string source = "<br>    专科检查<br>        心脏相对浊音界<br>        右(cm)    肋间    左(cm)<br>        2         II      2<br>        2         III     4<br>        3         IV      6<br>                 V       8<br>                 MCL     8.5";

        var result = new HtmlTextExtractionService().Extract(
            ToBase64(source, Encoding.UTF8),
            CreateProfile(),
            []);
        var lines = result.CleanedText.Split('\n');
        var header = Assert.Single(lines, line => line.Contains("右(cm)", StringComparison.Ordinal));
        var fourthRow = Assert.Single(lines, line => line.TrimStart().StartsWith("V ", StringComparison.Ordinal));
        var fifthRow = Assert.Single(lines, line => line.TrimStart().StartsWith("MCL ", StringComparison.Ordinal));

        Assert.Equal(header.IndexOf("肋间", StringComparison.Ordinal), fourthRow.IndexOf('V'));
        Assert.Equal(header.IndexOf("肋间", StringComparison.Ordinal), fifthRow.IndexOf("MCL", StringComparison.Ordinal));
        Assert.True(fourthRow.Take(fourthRow.IndexOf('V')).All(char.IsWhiteSpace));
        Assert.True(fifthRow.Take(fifthRow.IndexOf("MCL", StringComparison.Ordinal)).All(char.IsWhiteSpace));
    }

    [Fact]
    public void Extract_Utf8失败后使用Gb18030()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var profile = CreateProfile();
        var rules = new[]
        {
            new HtmlExtractionRule
            {
                FieldCode = "主诉",
                SourceSection = "主诉",
                ExtractionType = HtmlExtractionType.Section,
                IsRequired = true
            }
        };
        var encoding = Encoding.GetEncoding("GB18030");

        var result = new HtmlTextExtractionService().Extract(ToBase64("<br>主诉：测试内容", encoding), profile, rules);

        Assert.Equal("GB18030", result.EncodingName);
        Assert.Equal("测试内容", result.ExtractedFields["主诉"]);
    }

    [Fact]
    public void Extract_必填字段未命中_返回缺失字段名称()
    {
        var profile = CreateProfile();
        var rules = new[]
        {
            new HtmlExtractionRule
            {
                FieldCode = "血压",
                SourceSection = "体格检查",
                SourceLabel = "血压(BP)",
                ExtractionType = HtmlExtractionType.VitalSign,
                IsRequired = true
            }
        };

        var result = new HtmlTextExtractionService().Extract(
            ToBase64("<br>体格检查<br>一般情况良好", Encoding.UTF8),
            profile,
            rules);

        Assert.Null(result.ExtractedFields["血压"]);
        Assert.Equal(["血压"], result.MissingRequiredFields);
    }

    [Fact]
    public void Extract_解码内容超过限制_在转换前拒绝()
    {
        var profile = CreateProfile();
        profile.MaxInputBytes = 3;

        var exception = Assert.Throws<InvalidDataException>(() => new HtmlTextExtractionService().Extract(
            ToBase64("超过", Encoding.UTF8),
            profile,
            [new HtmlExtractionRule { FieldCode = "正文", Pattern = "(?<value>.*)", ExtractionType = HtmlExtractionType.Regex }]));

        Assert.Contains("超过限制", exception.Message);
    }

    private static EsbHtmlProfile CreateProfile() => new()
    {
        SourcePath = "$main.FILE_CONTENT",
        MaxInputBytes = 1024 * 1024,
        PreserveSections = true,
        SectionHeadings = HtmlProfileService.DefaultSectionHeadingsText
    };

    private static string ToBase64(string value, Encoding encoding)
        => Convert.ToBase64String(encoding.GetBytes(value));
}
