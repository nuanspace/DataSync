using DataSync.LHYY.V2.Services;
using Microsoft.AspNetCore.Mvc;

namespace DataSync.LHYY.V2.Controllers;

[ApiController]
[Route("api/active-medical-records")]
public class ActiveMedicalRecordsController : ControllerBase
{
    private readonly ActiveMedicalRecordService _activeMedicalRecordService;
    private readonly IntegrationProjectService _integrationProjectService;

    public ActiveMedicalRecordsController(
        ActiveMedicalRecordService activeMedicalRecordService,
        IntegrationProjectService integrationProjectService)
    {
        _activeMedicalRecordService = activeMedicalRecordService;
        _integrationProjectService = integrationProjectService;
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? integrationProjectCode,
        [FromQuery] int limit = 100,
        [FromQuery] long? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var projectCode = string.IsNullOrWhiteSpace(integrationProjectCode)
            ? await _integrationProjectService.GetCurrentProjectCodeAsync()
            : integrationProjectCode;

        var data = await _activeMedicalRecordService.GetActiveRecordsAsync(
            projectCode,
            limit,
            cursor,
            cancellationToken);

        return Ok(new
        {
            code = 0,
            message = "success",
            data
        });
    }
}
