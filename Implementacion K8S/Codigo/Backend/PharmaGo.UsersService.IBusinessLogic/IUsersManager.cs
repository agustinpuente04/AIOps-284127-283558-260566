using PharmaGo.Domain.Entities;

namespace PharmaGo.UsersService.IBusinessLogic
{
    public interface IUsersManager
    {
        public User CreateUser(string UserName, string UserCode, string Email, string Password, string Address, DateTime RegistrationDate);
    }
}

