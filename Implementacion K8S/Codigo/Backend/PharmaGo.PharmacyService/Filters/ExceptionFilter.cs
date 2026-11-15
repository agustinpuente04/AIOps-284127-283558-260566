using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc;
using PharmaGo.Exceptions;
using Microsoft.Data.SqlClient.Server;

namespace PharmaGo.PharmacyService.Filters;
public class ExceptionFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        try
        {
            throw context.Exception;
        }
        catch (ResourceNotFoundException e)
        {
            context.Result = new JsonResult(new {Message = e.Message }) { StatusCode = 404 };
        }
        catch (InvalidResourceException e)
        {
            context.Result = new JsonResult(new { Message = e.Message }) { StatusCode = 400 };
        }
        catch (FormatException e)
        {
            context.Result = new JsonResult(new { Message = "Invalid token format" }){ StatusCode = 400 };
        }
        catch (DatabaseUnavailableException e)
        {
            // La BD no respondió a tiempo o su circuit breaker está abierto: es una indisponibilidad temporal, no un error de la app
            var seconds = Math.Max(1, (int)Math.Ceiling(e.RetryAfter.TotalSeconds));
            context.HttpContext.Response.Headers["Retry-After"] = seconds.ToString();
            context.Result = new JsonResult(new { Message = "El servicio no puede acceder a la base de datos en este momento. Reintenta en unos segundos." }) { StatusCode = 503 };
        }
        catch (Exception e)
        {
            context.Result = new JsonResult(new { Message = e.Message }) { StatusCode = 500 };
        }
    }
}
