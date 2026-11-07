using InstrumentationInterface;
using Microsoft.AspNetCore.Mvc;
using PharmaGo.UsersService.IBusinessLogic;
using PharmaGo.UsersService.Models.In;
using PharmaGo.UsersService.Models.Out;

namespace PharmaGo.UsersService.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class LoginController : ControllerBase
    {
        private readonly ILoginManager _loginManager;
        private readonly ICustomMetrics _customMetrics;
        private readonly IStructuredLogger _structuredLogger;

       public LoginController(ILoginManager manager, ICustomMetrics customMetrics, IStructuredLogger structuredLogger)
       {
            _loginManager = manager;
            _customMetrics = customMetrics;
            _structuredLogger = structuredLogger;
       }

        [HttpPost]
        public IActionResult Login([FromBody] LoginModelRequest userModel)
        {
            _customMetrics.LoginInvocations();
            
            try
            {
                var authorization = _loginManager.Login(userModel.UserName, userModel.Password);
                
                _structuredLogger.LogInformation(
                    "Login succeeded",
                    new Dictionary<string, object>
                    {
                        ["pharma_biz"] = "login_success",
                        ["component"] = "LoginController",
                        ["operation"] = "login",
                        ["outcome"] = "success",
                        ["user_name"] = authorization.UserName,
                        ["user_id"] = authorization.UserId
                    });
                _customMetrics.RecordLogin("success");

                return Ok(new LoginModelResponse() { token = authorization.Token, role = authorization.Role, userName = authorization.UserName });
            }
            catch (Exception ex)
            {
                _structuredLogger.LogWarning(
                    "Login failed",
                    ex,
                    new Dictionary<string, object>
                    {
                        ["pharma_biz"] = "login_fail",
                        ["component"] = "LoginController",
                        ["operation"] = "login",
                        ["outcome"] = "failed",
                        ["user_name"] = userModel.UserName ?? "unknown"
                    });
                _customMetrics.RecordLogin("failed");

                throw;
            }
        }

    }
}

