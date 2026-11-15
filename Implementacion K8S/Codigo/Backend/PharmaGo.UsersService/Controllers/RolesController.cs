using Microsoft.AspNetCore.Mvc;
using PharmaGo.UsersService.IBusinessLogic;
using PharmaGo.UsersService.Models.Out;

namespace PharmaGo.UsersService.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class RolesController : ControllerBase
    {
        private readonly IRoleManager _roleManager;

        public RolesController(IRoleManager manager)
        {
            _roleManager = manager;
        }

        [HttpGet]
        public IActionResult GetAll()
        {
            var roles = _roleManager.GetAll();
            IEnumerable<RoleModelResponse> result = roles.Select(role => new RoleModelResponse(role));
            return Ok(result);
        }

        [HttpGet("test")]
        public IActionResult Test()
        {
            // Endpoint simple de prueba que siempre devuelve 200
            return Ok(new { status = "ok", message = "Service is healthy" });
        }

        [HttpGet("ping")]
        public IActionResult Ping()
        {
            // Endpoint simple de prueba que siempre devuelve 200 - sin dependencias
            return Ok(new { status = "ok", message = "pong", timestamp = DateTime.UtcNow });
        }

    }
}

