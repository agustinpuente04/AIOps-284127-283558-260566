using PharmaGo.Domain.Entities;
using PharmaGo.Domain.SearchCriterias;

namespace PharmaGo.PharmacyService.IBusinessLogic
{
    public interface IPharmacyManager
    {
        IEnumerable<Pharmacy> GetAll(PharmacySearchCriteria pharmacySearchCriteria);
        Pharmacy GetById(int id);
        Pharmacy Create(Pharmacy pharmacy);
        Pharmacy Update(int id, Pharmacy pharmacy);
        void Delete(int id);
    }
}

