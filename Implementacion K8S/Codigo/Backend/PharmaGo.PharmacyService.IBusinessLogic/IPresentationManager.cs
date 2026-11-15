using PharmaGo.Domain.Entities;

namespace PharmaGo.PharmacyService.IBusinessLogic
{
    public interface IPresentationManager
    {
        IEnumerable<Presentation> GetAll();
    }
}

