using Microsoft.AspNetCore.Mvc;
using PharmaGo.UsersService.IBusinessLogic;
using PharmaGo.UsersService.Models.In;
using PharmaGo.UsersService.Models.Out;

namespace PharmaGo.UsersService.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class UsersController : ControllerBase
    {
        private readonly IUsersManager _userManager;

        public UsersController(IUsersManager manager)
        {
            _userManager = manager;
        }

        [HttpPost]
        public IActionResult CreateUser([FromBody] UserModelRequest userModel)
        {
                var user = _userManager.CreateUser(userModel.UserName, userModel.UserCode,
                                                   userModel.Email, userModel.Password,
                                                   userModel.Address, userModel.RegistrationDate);
                var userModelResponse = new UserModelResponse(user);
                return Ok(userModelResponse);
            
        }
    }
}

