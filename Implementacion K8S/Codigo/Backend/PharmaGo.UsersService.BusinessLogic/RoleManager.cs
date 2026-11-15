using PharmaGo.Domain.Entities;
using PharmaGo.Domain.SearchCriterias;
using PharmaGo.Exceptions;
using PharmaGo.UsersService.IBusinessLogic;
using PharmaGo.IDataAccess;

namespace PharmaGo.UsersService.BusinessLogic
{
    public class RoleManager : IRoleManager
    {
        private readonly IRepository<Role> _roleRepository;

        public RoleManager(IRepository<Role> roleRepo)
        {
            _roleRepository = roleRepo;
        }

        public IEnumerable<Role> GetAll()
        {
            return _roleRepository.GetAllByExpression(expression => true);
        }
    }
}

