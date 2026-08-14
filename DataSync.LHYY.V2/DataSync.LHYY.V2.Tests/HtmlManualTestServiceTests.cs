using System.Text;
using DataSync.LHYY.V2.Models.Entities;
using DataSync.LHYY.V2.Models.Enums;
using DataSync.LHYY.V2.Models.Html;
using DataSync.LHYY.V2.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DataSync.LHYY.V2.Tests;

public sealed class HtmlManualTestServiceTests
{
    [Fact]
    public void Test_单独Base64_返回清洗文本和结构化结果()
    {
        var service = CreateService();

        var result = service.Test(
            ToBase64("&lt;br&gt;主诉：测试内容"),
            CreateProfile(),
            CreateRules(),
            null);

        Assert.Equal("单独 Base64", result.InputMode);
        var item = Assert.Single(result.Items);
        Assert.True(item.IsSuccess);
        Assert.Equal("主诉：测试内容", item.Extraction!.CleanedText);
        Assert.Equal("测试内容", item.Extraction.ExtractedFields["主诉"]);
    }

    [Fact]
    public void Test_未配置提取字段_仍可预览清洗文本和章节()
    {
        var result = CreateService().Test(
            ToBase64("&lt;br&gt;主诉：测试内容"),
            CreateProfile(),
            [],
            null);

        var item = Assert.Single(result.Items);
        Assert.True(item.IsSuccess);
        Assert.Equal("主诉：测试内容", item.Extraction!.CleanedText);
        Assert.Equal("测试内容", item.Extraction.Sections["主诉"]);
        Assert.Empty(item.Extraction.ExtractedFields);
    }

    [Fact]
    public void Test_完整数组响应_逐条解析且单条失败不影响其他记录()
    {
        var source = new JArray(
            new JObject { ["FILE_CONTENT"] = ToBase64("<br>主诉：第一条") },
            new JObject { ["FILE_CONTENT"] = "非法Base64" },
            new JObject { ["FILE_CONTENT"] = ToBase64("<br>主诉：第三条") });

        var result = CreateService().Test(
            source.ToString(Newtonsoft.Json.Formatting.None),
            CreateProfile(),
            CreateRules(),
            "$"
        );

        Assert.Equal(3, result.Items.Count);
        Assert.True(result.Items[0].IsSuccess);
        Assert.False(result.Items[1].IsSuccess);
        Assert.True(result.Items[2].IsSuccess);
        Assert.Equal("第一条", result.Items[0].Extraction!.ExtractedFields["主诉"]);
        Assert.Equal("第三条", result.Items[2].Extraction!.ExtractedFields["主诉"]);
    }

    [Fact]
    public void Test_嵌套主记录数组_使用当前数组路径和来源路径()
    {
        var root = new JObject
        {
            ["data"] = new JObject
            {
                ["records"] = new JArray(
                    new JObject { ["content"] = ToBase64("<br>主诉：嵌套记录") })
            }
        };
        var profile = CreateProfile();
        profile.SourcePath = "$main.content";

        var result = CreateService().Test(
            root.ToString(Newtonsoft.Json.Formatting.None),
            profile,
            CreateRules(),
            "$.data.records");

        var item = Assert.Single(result.Items);
        Assert.True(item.IsSuccess);
        Assert.Equal("嵌套记录", item.Extraction!.ExtractedFields["主诉"]);
    }

    [Fact]
    public void Test_Fbc027根数组_正式组合路径未命中时回退读取FileContent()
    {
        var root = new JArray(
            new JObject
            {
                ["HIS_KEY"] = "测试内容号",
                ["FILE_CONTENT"] = ToBase64("<br>主诉：原始数组")
            });
        var profile = CreateProfile();
        profile.SourcePath = "$main.FileContents[0].FILE_CONTENT";

        var result = CreateService().Test(
            root.ToString(Newtonsoft.Json.Formatting.None),
            profile,
            CreateRules(),
            "$.data");

        var item = Assert.Single(result.Items);
        Assert.True(item.IsSuccess);
        Assert.Equal("原始数组", item.Extraction!.ExtractedFields["主诉"]);
    }

    [Fact]
    public void Test_Fbc027根数组_单条缺少FileContent不阻断其他记录()
    {
        var root = new JArray(
            new JObject
            {
                ["HIS_KEY"] = "有效内容号",
                ["FILE_CONTENT"] = ToBase64("<br>主诉：有效记录")
            },
            new JObject { ["HIS_KEY"] = "缺少内容号" });
        var profile = CreateProfile();
        profile.SourcePath = "$main.FileContents[0].FILE_CONTENT";

        var result = CreateService().Test(
            root.ToString(Newtonsoft.Json.Formatting.None),
            profile,
            CreateRules(),
            "$.data");

        Assert.Equal(2, result.Items.Count);
        Assert.True(result.Items[0].IsSuccess);
        Assert.Equal("有效记录", result.Items[0].Extraction!.ExtractedFields["主诉"]);
        Assert.False(result.Items[1].IsSuccess);
        Assert.Contains("未从当前记录提取到 Base64 内容", result.Items[1].ErrorMessage);
    }

    [Fact]
    public void Test_Fbc027单对象_正式组合路径未命中时回退读取FileContent()
    {
        var root = new JObject
        {
            ["HIS_KEY"] = "测试内容号",
            ["FILE_CONTENT"] = ToBase64("<br>主诉：原始对象")
        };
        var profile = CreateProfile();
        profile.SourcePath = "$main.FileContents[0].FILE_CONTENT";

        var result = CreateService().Test(
            root.ToString(Newtonsoft.Json.Formatting.None),
            profile,
            CreateRules(),
            "$.data");

        var item = Assert.Single(result.Items);
        Assert.True(item.IsSuccess);
        Assert.Equal("原始对象", item.Extraction!.ExtractedFields["主诉"]);
    }

    [Fact]
    public void Test_Cyyy组合报文_继续严格使用正式路径()
    {
        var root = new JObject
        {
            ["serverCode"] = "JHIDS-BAS-IMR-025",
            ["data"] = new JArray(
                new JObject
                {
                    ["FileContents"] = new JArray(
                        new JObject
                        {
                            ["HIS_KEY"] = "测试内容号",
                            ["FILE_CONTENT"] = ToBase64("<br>主诉：组合报文")
                        })
                })
        };
        var profile = CreateProfile();
        profile.SourcePath = "$main.FileContents[0].FILE_CONTENT";

        var result = CreateService().Test(
            root.ToString(Newtonsoft.Json.Formatting.None),
            profile,
            CreateRules(),
            "$.data");

        var item = Assert.Single(result.Items);
        Assert.True(item.IsSuccess);
        Assert.Equal("组合报文", item.Extraction!.ExtractedFields["主诉"]);
    }

    [Fact]
    public void Test_非Fbc原始响应且主记录路径错误_仍报告配置错误()
    {
        var root = new JObject
        {
            ["records"] = new JArray(new JObject { ["content"] = "任意内容" })
        };
        var profile = CreateProfile();
        profile.SourcePath = "$main.FileContents[0].FILE_CONTENT";

        var exception = Assert.Throws<InvalidDataException>(() => CreateService().Test(
            root.ToString(Newtonsoft.Json.Formatting.None),
            profile,
            CreateRules(),
            "$.data"));

        Assert.Contains("主记录数组路径未命中数组：$.data", exception.Message);
    }

    [Fact]
    public void Test_根路径配置下单个Json对象_按单条记录解析()
    {
        var root = new JObject { ["FILE_CONTENT"] = ToBase64("<br>主诉：单对象") };

        var result = CreateService().Test(
            root.ToString(Newtonsoft.Json.Formatting.None),
            CreateProfile(),
            CreateRules(),
            "$");

        var item = Assert.Single(result.Items);
        Assert.True(item.IsSuccess);
        Assert.Equal("单对象", item.Extraction!.ExtractedFields["主诉"]);
    }

    [Fact]
    public void Test_看似Json但格式错误_不回退为Base64()
    {
        var exception = Assert.Throws<InvalidDataException>(() => CreateService().Test(
            "[{格式错误]",
            CreateProfile(),
            CreateRules(),
            "$"));

        Assert.Contains("JSON 解析失败", exception.Message);
    }

    [Fact]
    public void DiscoverCandidates_同时发现章节和标题值()
    {
        var service = CreateService();
        var result = service.Test(
            ToBase64("姓名：测试患者    性别：男\n入院时间：2026年08月13日10时20分\n主诉：胸部不适2天"),
            CreateProfile(),
            [],
            null);

        var candidates = service.DiscoverCandidates(result, []);

        var section = Assert.Single(candidates, candidate => candidate.FieldCode == "主诉");
        Assert.Equal(HtmlExtractionType.Section, section.ExtractionType);
        Assert.Equal("主诉", section.SourceSection);

        var name = Assert.Single(candidates, candidate => candidate.FieldCode == "姓名");
        Assert.Equal(HtmlExtractionType.LabelValue, name.ExtractionType);
        Assert.Equal("姓名", name.SourceLabel);
        Assert.Equal("测试患者", name.PreviewValue);

        var admissionTime = Assert.Single(candidates, candidate => candidate.FieldCode == "入院时间");
        Assert.Equal("2026年08月13日10时20分", admissionTime.PreviewValue);
    }

    [Fact]
    public void DiscoverCandidates_空标题值不串到下一行候选()
    {
        var service = CreateService();
        var result = service.Test(
            ToBase64("姓名：测试患者    职业：\n性别：男    工作单位：\n年龄：79"),
            CreateProfile(),
            [],
            null);

        var candidates = service.DiscoverCandidates(result, []);

        Assert.Null(Assert.Single(candidates, candidate => candidate.FieldCode == "职业").PreviewValue);
        Assert.Equal("男", Assert.Single(candidates, candidate => candidate.FieldCode == "性别").PreviewValue);
        Assert.Null(Assert.Single(candidates, candidate => candidate.FieldCode == "工作单位").PreviewValue);
        Assert.Equal("79", Assert.Single(candidates, candidate => candidate.FieldCode == "年龄").PreviewValue);
    }

    [Fact]
    public void DiscoverCandidates_已有同名字段标记为已配置()
    {
        var service = CreateService();
        var result = service.Test(
            ToBase64("主诉：测试内容"),
            CreateProfile(),
            [],
            null);

        var candidate = Assert.Single(service.DiscoverCandidates(result, CreateRules()));

        Assert.Equal("主诉", candidate.FieldCode);
        Assert.True(candidate.IsConfigured);
        Assert.False(candidate.IsSelected);
    }

    [Fact]
    public void MergeCandidateRules_保留已有规则并按字段名去重()
    {
        var existing = CreateRules();
        var candidates = new List<HtmlFieldCandidate>
        {
            new()
            {
                FieldCode = "主诉",
                SourceSection = "主诉",
                ExtractionType = HtmlExtractionType.Section,
                IsSelected = true
            },
            new()
            {
                FieldCode = "入院时间",
                SourceLabel = "入院时间",
                ExtractionType = HtmlExtractionType.LabelValue,
                IsSelected = true
            }
        };

        var merged = HtmlManualTestService.MergeCandidateRules(existing, candidates);

        Assert.Equal(2, merged.Count);
        Assert.Equal("主诉", merged[0].FieldCode);
        Assert.True(merged[0].IsRequired);
        Assert.Equal("入院时间", merged[1].FieldCode);
        Assert.Equal(HtmlExtractionType.LabelValue, merged[1].ExtractionType);
        Assert.Equal([0, 1], merged.Select(rule => rule.SortOrder));
    }

    private static HtmlManualTestService CreateService()
        => new(new HtmlTextExtractionService());

    private static EsbHtmlProfile CreateProfile() => new()
    {
        SourcePath = "$main.FILE_CONTENT",
        MaxInputBytes = 1024 * 1024,
        PreserveSections = true,
        SectionHeadings = HtmlProfileService.DefaultSectionHeadingsText
    };

    private static List<HtmlExtractionRule> CreateRules() =>
    [
        new HtmlExtractionRule
        {
            FieldCode = "主诉",
            SourceSection = "主诉",
            ExtractionType = HtmlExtractionType.Section,
            IsRequired = true
        }
    ];

    private static string ToBase64(string value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
}
