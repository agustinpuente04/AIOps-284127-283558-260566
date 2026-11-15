using Microsoft.AspNetCore.Mvc;
using PharmaGo.Domain.Entities;
using PharmaGo.PharmacyService.IBusinessLogic;
using PharmaGo.PharmacyService.Models.Out;

namespace PharmaGo.PharmacyService.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class UnitMeasuresController : ControllerBase
    {
        private readonly IUnitMeasureManager _unitMeasureManager;

        public UnitMeasuresController(IUnitMeasureManager manager)
        {
            _unitMeasureManager = manager;
        }

        [HttpGet]
        public IActionResult GetAll()
        {
            var unitMeasureList = _unitMeasureManager.GetAll();
            IEnumerable<UnitMeasureBasicModel> unitMeasuresToReturn = unitMeasureList.Select(d => new UnitMeasureBasicModel(d));
            return Ok(unitMeasuresToReturn);
        }

    }
}
