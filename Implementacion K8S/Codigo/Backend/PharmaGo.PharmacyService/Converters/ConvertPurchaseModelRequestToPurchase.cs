using PharmaGo.Domain.Entities;
using PharmaGo.PharmacyService.Models.In;

namespace PharmaGo.PharmacyService.Converters
{
    public class PurchaseModelRequestToPurchaseConverter
    {

        public Purchase Convert(PurchaseModelRequest model)
        {

            var purchase = new Purchase();
            purchase.PurchaseDate = model.PurchaseDate;
            purchase.BuyerEmail = model.BuyerEmail;
            purchase.details = new List<PurchaseDetail>();
            foreach (var detail in model.Details)
            {
                purchase.details
                    .Add(new PurchaseDetail
                    {
                        Quantity = detail.Quantity,
                        Drug = new Drug { Code = detail.Code },
                        Pharmacy = new()
                        {
                            Id = detail.PharmacyId
                        }
                    });
            }

            return purchase;
        }

    }
}
