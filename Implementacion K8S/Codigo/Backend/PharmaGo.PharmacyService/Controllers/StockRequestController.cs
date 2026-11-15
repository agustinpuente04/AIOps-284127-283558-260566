using InstrumentationInterface;
using Microsoft.AspNetCore.Mvc;
using PharmaGo.Domain.Entities;
using PharmaGo.Exceptions;
using PharmaGo.PharmacyService.IBusinessLogic;
using PharmaGo.PharmacyService.Enums;
using PharmaGo.PharmacyService.Filters;
using PharmaGo.PharmacyService.Models.In;
using PharmaGo.PharmacyService.Models.Out;

namespace PharmaGo.PharmacyService.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class StockRequestController : ControllerBase
    {
        private readonly IStockRequestManager _stockRequestManager;
        private readonly ICustomMetrics _customMetrics;

        public StockRequestController(IStockRequestManager manager, ICustomMetrics customMetrics)
        {
            _stockRequestManager = manager;
            _customMetrics = customMetrics;
        }

        [HttpPost]
        [AuthorizationFilter(new string[] { nameof(RoleType.Employee) })]
        public IActionResult CreateStockRequest([FromBody] StockRequestModelRequest stockRequestModel)
        {
            string token = HttpContext.Request.Headers["Authorization"];
            var stockRequest = Track("create", () => _stockRequestManager.CreateStockRequest(stockRequestModel.ToEntity(), token));
            return Ok(new StockRequestModelResponse(stockRequest));
        }

        [HttpPut]
        [AuthorizationFilter(new string[] { nameof(RoleType.Owner) })]
        [Route("[action]/{id}")]
        public IActionResult ApproveStockRequest(int id)
        {
            var approved = Track("approve", () => _stockRequestManager.ApproveStockRequest(id));
            return Ok(approved);
        }

        [HttpPut]
        [Route("[action]/{id}")]
        [AuthorizationFilter(new string[] { nameof(RoleType.Owner) })]
        public IActionResult RejectStockRequest(int id)
        {
            var approved = Track("reject", () => _stockRequestManager.RejectStockRequest(id));
            return Ok(approved);
        }

        /// <summary>
        /// Ejecuta la operación y registra pharmago_stock_requests_total{action, outcome}.
        /// La excepción se relanza para que la maneje el ExceptionFilter como antes.
        /// </summary>
        private T Track<T>(string action, Func<T> operation)
        {
            try
            {
                var result = operation();
                _customMetrics.RecordStockRequest(action, "success");
                return result;
            }
            catch
            {
                _customMetrics.RecordStockRequest(action, "failed");
                throw;
            }
        }

        [HttpGet]
        [Route("[action]")]
        [AuthorizationFilter(new string[] { nameof(RoleType.Employee) })]
        public IActionResult ByEmployee([FromQuery] StockRequestSearchCriteriaModelRequest searchCriteria)
        {
            string token = HttpContext.Request.Headers["Authorization"];
            var stockRequests = _stockRequestManager.GetStockRequestsByEmployee(token, searchCriteria.ToEntity());

            IEnumerable<StockRequestSearchCriteriaModelResponse> result = stockRequests.Select(d => new StockRequestSearchCriteriaModelResponse(d));
            return Ok(result);
        }

        [HttpGet]
        [AuthorizationFilter(new string[] { nameof(RoleType.Owner) })]
        public IActionResult GetAll()
        {
            string token = HttpContext.Request.Headers["Authorization"];
            var stockRequests = _stockRequestManager.GetStockRequestsByOwner(token);

            IEnumerable<StockRequestSearchCriteriaModelResponse> result = stockRequests.Select(d => new StockRequestSearchCriteriaModelResponse(d));
            return Ok(result);
        }
    }
}
