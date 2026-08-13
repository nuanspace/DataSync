using DataSync.LHYY.V2.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace DataSync.LHYY.V2.Tests;

public sealed class JsonFieldTranslationServiceTests
{
    [Theory]
    [InlineData("http://localhost:11434", "http://localhost:11434/v1/chat/completions")]
    [InlineData("http://localhost:11434/", "http://localhost:11434/v1/chat/completions")]
    [InlineData("http://localhost:11434/v1", "http://localhost:11434/v1/chat/completions")]
    [InlineData("http://localhost:11434/v1/", "http://localhost:11434/v1/chat/completions")]
    [InlineData("http://localhost:11434/v1/chat/completions", "http://localhost:11434/v1/chat/completions")]
    [InlineData("http://localhost:11434/proxy", "http://localhost:11434/proxy/chat/completions")]
    [InlineData("https://openrouter.ai/api", "https://openrouter.ai/api/v1/chat/completions")]
    public void BuildChatCompletionsUrl_兼容常见基础地址格式(string baseUrl, string expected)
    {
        Assert.Equal(expected, LlmService.BuildChatCompletionsUrl(baseUrl));
    }

    [Theory]
    [InlineData("google/gemini-2.5-flash-lite-preview-09-2025", "google/gemini-2.5-flash-lite")]
    [InlineData(" google/gemini-2.5-flash-lite ", "google/gemini-2.5-flash-lite")]
    [InlineData("custom/model", "custom/model")]
    public void NormalizeModelName_迁移已停用别名并保留自定义模型(string model, string expected)
    {
        Assert.Equal(expected, LlmService.NormalizeModelName(model));
    }

    [Fact]
    public void BuildCandidates_数组字段路径归一化并去重()
    {
        var root = JToken.Parse("""
            {
              "data": [
                { "HIS_PAT_ID": "P001", "TAKE_IN": "温水" },
                { "HIS_PAT_ID": "P002", "TAKE_IN": "凉水" }
              ]
            }
            """);

        var candidates = JsonFieldTranslationService.BuildCandidates(root);

        Assert.Contains(candidates, item => item.Path == "data");
        Assert.Single(candidates, item => item.Path == "data[].HIS_PAT_ID");
        Assert.Single(candidates, item => item.Path == "data[].TAKE_IN");
        Assert.DoesNotContain(candidates, item => item.Path.Contains("[0]", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildBuiltInTranslations_按归一化路径匹配公共词典()
    {
        var root = JToken.Parse("""
            {
              "data": [
                { "HIS_PAT_ID": "P001", "DELETE_FLAG": "0" }
              ]
            }
            """);

        var translations = JsonFieldTranslationService.BuildBuiltInTranslations(root);

        Assert.Equal("数据", translations["data"]);
        Assert.Equal("HIS患者编号", translations["data[].HIS_PAT_ID"]);
        Assert.Equal("删除标志", translations["data[].DELETE_FLAG"]);
    }

    [Theory]
    [InlineData("data[0].HIS_PAT_ID", "data[].HIS_PAT_ID")]
    [InlineData("data[12].items[3].code", "data[].items[].code")]
    public void NormalizePath_只替换数组数字索引(string path, string expected)
    {
        Assert.Equal(expected, JsonFieldTranslationService.NormalizePath(path));
    }

    [Fact]
    public void SanitizeSampleValue_隐藏敏感值并保留安全枚举示例()
    {
        var patientCode = JsonFieldTranslationService.SanitizeSampleValue(
            "HIS_PAT_ID",
            "data[0].HIS_PAT_ID",
            JValue.CreateString("V002_202604211338"));
        var unknownShortText = JsonFieldTranslationService.SanitizeSampleValue(
            "XM",
            "data[0].XM",
            JValue.CreateString("张三"));
        var safeEnum = JsonFieldTranslationService.SanitizeSampleValue(
            "TAKE_IN_WAY",
            "data[0].TAKE_IN_WAY",
            JValue.CreateString("口服"));
        var dateTime = JsonFieldTranslationService.SanitizeSampleValue(
            "CREATED_T",
            "data[0].CREATED_T",
            JValue.CreateString("2026-04-21 12:38:02"));

        Assert.Equal("<敏感示例已隐藏>", patientCode);
        Assert.Equal("<文本示例已隐藏，长度 2>", unknownShortText);
        Assert.Equal("口服", safeEnum);
        Assert.Equal("<日期时间>", dateTime);
    }

    [Fact]
    public void ParseChineseFieldTranslations_只接收请求范围内的合法结果()
    {
        var response = """
            返回结果：
            ```json
            [
              { "path": "data[].TAKE_IN", "chineseName": "摄入内容" },
              { "path": "other", "chineseName": "越界字段" },
              { "path": "data[].TAKE_OUT", "chineseName": "包含\n换行" }
            ]
            ```
            """;
        var allowedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "data[].TAKE_IN",
            "data[].TAKE_OUT"
        };

        var translations = LlmService.ParseChineseFieldTranslations(response, allowedPaths);

        Assert.Single(translations);
        Assert.Equal("摄入内容", translations["data[].TAKE_IN"]);
    }

    [Fact]
    public void ParseChineseFieldTranslations_非法响应返回空结果()
    {
        var translations = LlmService.ParseChineseFieldTranslations(
            "模型未返回结构化内容",
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "data.code" });

        Assert.Empty(translations);
    }
}
