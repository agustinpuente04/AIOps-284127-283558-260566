using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using PharmaGo.IDataAccess;
using PharmaGo.Domain.Entities;
using PharmaGo.Exceptions;

namespace PharmaGo.PharmacyService.Filters
{
    public class AuthorizationFilter : Attribute, IActionFilter
    {

        private readonly string[] _roles;

        public AuthorizationFilter(string[] roles)
        {
            _roles = roles;
        }

        public void OnActionExecuted(ActionExecutedContext context)
        {
        }

        public void OnActionExecuting(ActionExecutingContext context)
        {
            string token = context.HttpContext.Request.Headers["Authorization"];
            if (String.IsNullOrEmpty(token) || !IsTokenValid(context, token))
            {
                context.Result = new JsonResult(new { Message = "Invalid authorization token" })
                { StatusCode = 401 };
            } else if (!IsRoleValid(context, _roles, token))
            {
                context.Result = new JsonResult(new { Message = "Forbidden role" })
                { StatusCode = 403 };
            }
        }

        private static bool IsTokenValid(ActionExecutingContext context, string token)
        {
            try
            {
                var guidToken = new Guid(token);
                var sessionRepository = (IRepository<Session>)context.HttpContext.RequestServices.GetService(typeof(IRepository<Session>));
                Session session = sessionRepository.GetOneByExpression(x => x.Token == guidToken);
                return session != null;
            }
            catch (DatabaseUnavailableException)
            {
                throw;   // una BD caída no es "token inválido": que responda 503 el ExceptionFilter
            }
            catch
            {
                return false;
            }
        }

        private static bool IsRoleValid(ActionExecutingContext context, string[] roles, string token)
        {
            try
            {
                var guidToken = new Guid(token);
                var sessionRepository = (IRepository<Session>)context.HttpContext.RequestServices.GetService(typeof(IRepository<Session>));
                var userRepository = (IRepository<User>)context.HttpContext.RequestServices.GetService(typeof(IRepository<User>));
                
                Session session = sessionRepository.GetOneByExpression(x => x.Token == guidToken);
                if (session == null) return false;
                
                var userId = session.UserId;
                User user = userRepository.GetOneDetailByExpression(x => x.Id == userId);
                if (user == null) return false;
                
                foreach(string role in roles)
                {
                    if (user.Role.Name.ToLower() == role.ToLower()) return true;
                }
                return false;
            }
            catch (DatabaseUnavailableException)
            {
                throw;   // una BD caída no es "token inválido": que responda 503 el ExceptionFilter
            }
            catch
            {
                return false;
            }
        }

    }
}
