using InstrumentationInterface;
using Microsoft.AspNetCore.Mvc;
using PharmaGo.Exceptions;
using PharmaGo.PharmacyService.IBusinessLogic;
using PharmaGo.PharmacyService.Converters;
using PharmaGo.PharmacyService.Enums;
using PharmaGo.PharmacyService.Filters;
using PharmaGo.PharmacyService.Models.In;
using PharmaGo.PharmacyService.Models.Out;

namespace PharmaGo.PharmacyService.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class PurchasesController : ControllerBase
    {
        private readonly IPurchasesManager _purchasesManager;
        private readonly IStructuredLogger _structuredLogger;
        private readonly ICustomMetrics _customMetrics;

        public PurchasesController(IPurchasesManager manager, IStructuredLogger structuredLogger, ICustomMetrics customMetrics)
        {
            _purchasesManager = manager;
            _structuredLogger = structuredLogger;
            _customMetrics = customMetrics;
        }

        [HttpGet]
        [AuthorizationFilter(new string[] { nameof(RoleType.Employee) })]
        public IActionResult All()
        {
            string token = HttpContext.Request.Headers["Authorization"];
            var retrievedPuerchases = _purchasesManager.GetAllPurchases(token)
                .Select(p => new PurchaseModelResponse(p)).ToList();
            return Ok(retrievedPuerchases);

        }

        [HttpGet]
        [Route("[action]")]
        [AuthorizationFilter(new string[] { nameof(RoleType.Owner) })]
        public IActionResult ByDate([FromQuery] DateTime? start, [FromQuery] DateTime? end)
        {
            string token = HttpContext.Request.Headers["Authorization"];
            var retrievedPuerchases = _purchasesManager.GetAllPurchasesByDate(token, start, end)
                .Select(p => new PurchaseModelResponse(p)).ToList();
            return Ok(retrievedPuerchases);
        }

        [HttpPut]
        [Route("[action]/{id}")]
        [AuthorizationFilter(new string[] { nameof(RoleType.Employee) })]
        public IActionResult Approve(int id, [FromBody] PurchaseAuthorizationModel model)
        {
            var purchaseDetail = _purchasesManager.ApprobePurchaseDetail(id, model.pharmacyId, model.drugCode);
            var purchaseDetailModelResponse = new PurchaseDetailModelResponse(id, purchaseDetail);
            return Ok(purchaseDetailModelResponse);
        }

        [HttpPut]
        [Route("[action]/{id}")]
        [AuthorizationFilter(new string[] { nameof(RoleType.Employee) })]
        public IActionResult Reject(int id, [FromBody] PurchaseAuthorizationModel model)
        {
            var purchaseDetail = _purchasesManager.RejectPurchaseDetail(id, model.pharmacyId, model.drugCode);
            var purchaseDetailModelResponse = new PurchaseDetailModelResponse(id, purchaseDetail);
            return Ok(purchaseDetailModelResponse);
        }

        [HttpPost]
        public IActionResult CreatePurchase([FromBody] PurchaseModelRequest purchaseModel)
        {
            try
            {
                var converter = new PurchaseModelRequestToPurchaseConverter();
                var purchase = _purchasesManager.CreatePurchase(converter.Convert(purchaseModel));
                _structuredLogger.LogInformation(
                    "Purchase created",
                    new Dictionary<string, object>
                    {
                        ["pharma_biz"] = "purchase_create",
                        ["component"] = "PurchasesController",
                        ["operation"] = "create_purchase",
                        ["outcome"] = "success",
                        ["order_id"] = purchase.Id,
                        ["tracking_code"] = purchase.TrackingCode,
                        ["buyer_email"] = purchase.BuyerEmail
                    });
                _customMetrics.RecordPurchase("success", purchase.TotalAmount);
                var purchaseModelResponse = new PurchaseModelResponse(purchase);
                return Ok(purchaseModelResponse);
            }
            catch (DatabaseUnavailableException)
            {
                _customMetrics.RecordPurchase("unavailable", 0);
                throw;
            }
            catch (Exception ex)
            {
                _structuredLogger.LogWarning(
                    "Purchase creation failed",
                    ex,
                    new Dictionary<string, object>
                    {
                        ["pharma_biz"] = "purchase_create_fail",
                        ["component"] = "PurchasesController",
                        ["operation"] = "create_purchase",
                        ["outcome"] = "failed",
                        ["buyer_email"] = purchaseModel?.BuyerEmail ?? ""
                    });
                _customMetrics.RecordPurchase("failed", 0);
                throw;
            }
        }

        [HttpGet]
        [Route("[action]")]
        public IActionResult Tracking([FromQuery] string? Code)
        {
            var purchase = _purchasesManager.GetPurchaseByTrackingCode(Code);
            var purchaseModelResponse = new PurchaseModelResponse(purchase);
            return Ok(purchaseModelResponse);
        }
    }
}
