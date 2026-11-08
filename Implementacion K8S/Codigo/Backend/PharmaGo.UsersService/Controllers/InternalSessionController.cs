using Microsoft.AspNetCore.Mvc;
using PharmaGo.Domain.Entities;
using PharmaGo.IDataAccess;

namespace PharmaGo.UsersService.Controllers
{
    /// <summary>
    /// Endpoint de uso interno: el API Gateway lo consulta para saber si el token de una request es una sesión real
    /// y de qué usuario es, y así aplicar el rate limit POR USUARIO en lugar de por IP.
    ///
    /// No está publicado por el gateway (ninguna ruta de YARP apunta a /internal) ni por nginx (solo proxia /api/):
    /// solo es alcanzable desde dentro del cluster. Aun así no expone nada que un token válido no permita: un token
    /// es un GUID de 122 bits, no se puede adivinar probando.
    /// </summary>
    [Route("internal/session")]
    [ApiController]
    public class InternalSessionController : ControllerBase
    {
        private readonly IRepository<Session> _sessionRepository;
        private readonly IRepository<User> _userRepository;

        public InternalSessionController(IRepository<Session> sessionRepository, IRepository<User> userRepository)
        {
            _sessionRepository = sessionRepository;
            _userRepository = userRepository;
        }

        [HttpGet("validate")]
        public IActionResult Validate([FromHeader(Name = "Authorization")] string? authorization)
        {
            if (!Guid.TryParse(authorization, out var token)) return Unauthorized();

            var session = _sessionRepository.GetOneByExpression(s => s.Token == token);
            if (session == null) return Unauthorized();

            var user = _userRepository.GetOneDetailByExpression(u => u.Id == session.UserId);
            if (user == null) return Unauthorized();

            return Ok(new { userId = user.Id, role = user.Role?.Name ?? "" });
        }
    }
}
