using Microsoft.AspNetCore.Mvc;
using PharmaGo.Domain.Entities;
using PharmaGo.PharmacyService.IBusinessLogic;
using PharmaGo.PharmacyService.Models.Out;

namespace PharmaGo.PharmacyService.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class PresentationsController : ControllerBase
    {
        private readonly IPresentationManager _presentationManager;

        public PresentationsController(IPresentationManager manager)
        {
            _presentationManager = manager;
        }

        [HttpGet]
        public IActionResult GetAll()
        {
            IEnumerable<Presentation> presentationList = _presentationManager.GetAll();
            IEnumerable<PresentationBasicModel> presentationsToReturn = presentationList.Select(d => new PresentationBasicModel(d));
            return Ok(presentationsToReturn);
        }

    }
}
