using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PharmaGo.UsersService.BusinessLogic;
using PharmaGo.DataAccess;
using PharmaGo.DataAccess.Repositories;
using PharmaGo.DataAccess.Resilience;
using PharmaGo.Domain.Entities;
using PharmaGo.UsersService.IBusinessLogic;
using PharmaGo.IDataAccess;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using InstrumentationInterface;
using Instrumentation;
using PharmaGo.Common.Resilience;

namespace PharmaGo.UsersService.Factory
{
    public static class ServiceFactory
    {

        public static void RegisterBusinessLogicServices(this IServiceCollection serviceCollection, IConfiguration configuration)
        {
            serviceCollection.AddScoped<ILoginManager, LoginManager>();
            serviceCollection.AddScoped<IUsersManager, UsersManager>();
            serviceCollection.AddScoped<IInvitationManager, InvitationManager>();
            serviceCollection.AddScoped<IRoleManager, RoleManager>();
            
            serviceCollection.AddHttpClient<PharmaGo.UsersService.HttpClients.PharmacyServiceClient>(client =>
            {
                var serviceUrl = configuration["ServiceUrls:PharmacyService"] ?? "http://127.0.0.1:5002";
                client.BaseAddress = new Uri(serviceUrl);
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddPolicyHandler(ResiliencePolicies.GetCircuitBreakerPolicy("pharmacy-service"))
            .AddPolicyHandler(ResiliencePolicies.GetRetryWithJitterPolicy("pharmacy-service"));
        }

        public static void RegisterDataAccessServices(this IServiceCollection serviceCollection, IConfiguration configuration)
        {
            // Resiliencia del acceso a la BD: timeout corto + circuit breaker por entidad + retry con equal jitter.
            // Ver Backend/RESILIENCIA_BD.md.
            var resilience = DbResilienceOptions.FromConfiguration(key => configuration[key]);
            serviceCollection.AddDbResilience(resilience, sp => new MetricsDbResilienceObserver(sp.GetRequiredService<ILoggerFactory>()));

            serviceCollection.AddResilientRepository<User, UsersRepository>();
            serviceCollection.AddResilientRepository<Session, SessionRepository>();
            serviceCollection.AddResilientRepository<Invitation, InvitationRepository>();
            serviceCollection.AddResilientRepository<Role, RoleRepository>();
            serviceCollection.AddResilientRepository<Pharmacy, PharmacyRepository>();

            serviceCollection.AddDbContext<DbContext, PharmacyGoDbContext>(options =>
            {
                // CommandTimeout corto: las consultas de EF son síncronas y solo SqlClient puede cortarlas. Con el valor por defecto
                // (30 s) una tabla bloqueada ocupa un hilo y una conexión medio minuto por request (escenario C4).
                options.UseSqlServer(
                    resilience.ApplyConnectTimeout(configuration.GetConnectionString("DefaultConnection")),
                    sqlOptions =>
                    {
                        sqlOptions.MigrationsAssembly("PharmaGo.DataAccess");
                        sqlOptions.CommandTimeout(resilience.CommandTimeoutSeconds);
                    });
                options.ConfigureWarnings(w =>
                {
                    w.Ignore(RelationalEventId.CommandExecuted);
                    w.Ignore(RelationalEventId.CommandError);
                });
            });
            serviceCollection.AddSingleton<ICustomMetrics, CustomMetrics>();
            serviceCollection.AddSingleton<IStructuredLogger, StructuredLogger>();
        }

        public static IHost MigrateDatabase(this IHost host)
        {
            using (var scope = host.Services.CreateScope())
            {
                var dbContext = scope.ServiceProvider.GetRequiredService<DbContext>();
                // Las migraciones pueden tardar más que el timeout corto de las consultas normales
                var resilience = scope.ServiceProvider.GetRequiredService<DbResilienceOptions>();
                dbContext.Database.SetCommandTimeout(TimeSpan.FromSeconds(resilience.MigrationTimeoutSeconds));
                dbContext.Database.Migrate();
            }

            return host;
        }

    }
}
