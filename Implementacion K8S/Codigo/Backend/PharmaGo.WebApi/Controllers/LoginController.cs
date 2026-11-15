using InstrumentationInterface;
using Microsoft.AspNetCore.Mvc;
using PharmaGo.IBusinessLogic;
using PharmaGo.WebApi.Models.In;
using PharmaGo.WebApi.Models.Out;

namespace PharmaGo.WebApi.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class LoginController : ControllerBase
    {
        private readonly ILoginManager _loginManager;
        private readonly ICustomMetrics _customMetrics;

       public LoginController(ILoginManager manager, ICustomMetrics customMetrics)
       {
            _loginManager = manager;
            _customMetrics = customMetrics;
       }

        [HttpPost]
        public IActionResult Login([FromBody] LoginModelRequest userModel)
        {
            _customMetrics.LoginInvocations();
            var authorization = _loginManager.Login(userModel.UserName, userModel.Password);
            return Ok(new LoginModelResponse() { token = authorization.Token, role = authorization.Role, userName = authorization.UserName });
        }

    }
}
