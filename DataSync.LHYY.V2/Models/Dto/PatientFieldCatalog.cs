namespace DataSync.LHYY.V2.Models.Dto;

public sealed record PatientFieldDefinition(string Name, string Label, string DataType);

public static class PatientFieldCatalog
{
    // Business-facing fields aligned to public.patient columns.
    public static readonly IReadOnlyList<PatientFieldDefinition> Definitions =
    [
        new("medical_record_number", "病案号", "string"),
        new("inpatient_number", "住院号", "string"),
        new("name", "姓名", "string"),
        new("gender", "性别", "string"),
        new("birthday", "出生日期", "DateOnly"),
        new("age", "年龄", "int"),
        new("sid_type", "证件类型", "string"),
        new("sid_number", "证件号码", "string"),
        new("phone_type", "电话类型", "string"),
        new("phone_number", "联系电话", "string"),
        new("weixin_id", "微信号", "string"),
        new("weixin_phone_number", "微信绑定手机号", "string"),
        new("contact_name", "联系人姓名", "string[]"),
        new("contact_phone", "联系人电话", "string[]"),
        new("contact_relation", "联系人关系", "string[]"),
        new("address_type", "地址类型", "string[]"),
        new("address_content", "地址内容", "string[]"),
        new("email", "电子邮箱", "string[]"),
        new("nation", "民族", "string"),
        new("native_place", "籍贯", "string"),
        new("marital_status", "婚姻状况", "string"),
        new("education", "文化程度", "string"),
        new("occupation", "职业", "string"),
        new("death_time", "死亡时间", "DateTime"),
        new("death_cause", "死亡原因", "string"),
    ];
}
