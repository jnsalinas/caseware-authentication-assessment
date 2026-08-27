namespace Collaborate.Authorization.Api.Controllers;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;

[ApiController]
[Route("api/documents")]
public class DocumentsController : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = "DocumentRead")]
    public IActionResult GetDocuments()
    {
        return Ok(new { message = "You are authorized to access this resource. Your scope is: documents.read, enjoy your connection!" });
    }
}