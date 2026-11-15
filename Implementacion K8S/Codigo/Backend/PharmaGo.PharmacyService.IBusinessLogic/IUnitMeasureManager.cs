using PharmaGo.Domain.Entities;

namespace PharmaGo.PharmacyService.IBusinessLogic
{
    public interface IUnitMeasureManager
    {
        IEnumerable<UnitMeasure> GetAll();
    }
}

