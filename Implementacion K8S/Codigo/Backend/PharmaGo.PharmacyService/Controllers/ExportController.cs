using Microsoft.AspNetCore.Mvc;
using PharmaGo.PharmacyService.IBusinessLogic;
using PharmaGo.PharmacyService.Enums;
using PharmaGo.PharmacyService.Filters;
using PharmaGo.PharmacyService.Models.In.Exports;

namespace PharmaGo.PharmacyService.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [TypeFilter(typeof(ExceptionFilter))]
    [AuthorizationFilter(new string[] { nameof(RoleType.Employee) })]
    public class ExportController : Controller
    {
        private readonly IExportManager _exportManager;

        public ExportController(IExportManager transactionManager)
        {
            _exportManager = transactionManager;
        }

        [HttpGet("exporters")]
        public IActionResult GetAllExporters()
        {
            return Ok(_exportManager.GetAllExporters());
        }

        [HttpGet("parameters")]
        public IActionResult GetParameters([FromQuery] string exporterName)
        {
            return Ok(_exportManager.GetParameters(exporterName));
        }

        [HttpPost]
        public IActionResult ExportDrugs([FromBody] DrugsExportationModel drugExportationModel)
        {
            string token = HttpContext.Request.Headers["Authorization"];
            _exportManager.ExportDrugs(drugExportationModel.FormatName, drugExportationModel.Parameters, token);
            return Ok(true);
        }
    }
}
