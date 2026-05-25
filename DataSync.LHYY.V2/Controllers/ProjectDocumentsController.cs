using System.Text;
using DataSync.LHYY.V2.Services;
using Microsoft.AspNetCore.Mvc;

namespace DataSync.LHYY.V2.Controllers;

/// <summary>
/// 项目文档浏览器预览入口
/// </summary>
[ApiController]
[Route("project-documents")]
public class ProjectDocumentsController : ControllerBase
{
    private readonly ProjectDocumentService _projectDocumentService;

    public ProjectDocumentsController(ProjectDocumentService projectDocumentService)
    {
        _projectDocumentService = projectDocumentService;
    }

    [HttpGet("view/{id:int}")]
    public async Task<IActionResult> ViewAsync(int id)
    {
        var document = await _projectDocumentService.GetDocumentAsync(id);
        if (document == null || document.IsDeleted)
        {
            return NotFound("文档不存在");
        }

        if (_projectDocumentService.IsWordDocument(document))
        {
            var html = _projectDocumentService.BuildBrowserPreviewHtml(document);
            return Content(html, "text/html; charset=utf-8", Encoding.UTF8);
        }

        if (_projectDocumentService.CanPreview(document))
        {
            return Redirect(_projectDocumentService.GetPreviewUrl(document));
        }

        return Redirect(_projectDocumentService.GetPreviewUrl(document));
    }
}
