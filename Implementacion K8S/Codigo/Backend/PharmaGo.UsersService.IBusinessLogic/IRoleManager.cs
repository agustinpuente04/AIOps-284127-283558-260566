using PharmaGo.Domain.Entities;

namespace PharmaGo.UsersService.IBusinessLogic
{
    public interface IRoleManager
    {
        IEnumerable<Role> GetAll();
    }
}

