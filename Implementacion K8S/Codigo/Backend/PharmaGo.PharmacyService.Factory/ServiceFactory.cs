using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PharmaGo.PharmacyService.BusinessLogic;
using PharmaGo.DataAccess;
using PharmaGo.DataAccess.Repositories;
using PharmaGo.DataAccess.Resilience;
using PharmaGo.Domain.Entities;
using PharmaGo.PharmacyService.IBusinessLogic;
using PharmaGo.IDataAccess;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using InstrumentationInterface;
using Instrumentation;
using PharmaGo.Common.Resilience;

namespace PharmaGo.PharmacyService.Factory
{
    public static class ServiceFactory
    {

        public static void RegisterBusinessLogicServices(this IServiceCollection serviceCollection, IConfiguration configuration)
        {
            serviceCollection.AddScoped<IStockRequestManager, StockRequestManager>();
            serviceCollection.AddScoped<IPurchasesManager, PurchasesManager>();
            serviceCollection.AddScoped<IPharmacyManager, PharmacyManager>();
            serviceCollection.AddScoped<IDrugManager, DrugManager>();
            serviceCollection.AddScoped<IPresentationManager, PresentationManager>();
            serviceCollection.AddScoped<IUnitMeasureManager, UnitMeasureManager>();
            serviceCollection.AddScoped<IExportManager, ExportManager>();
            
            serviceCollection.AddHttpClient<PharmaGo.PharmacyService.HttpClients.UsersServiceClient>(client =>
            {
                var serviceUrl = configuration["ServiceUrls:UsersService"] ?? "http://127.0.0.1:5001";
                client.BaseAddress = new Uri(serviceUrl);
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddPolicyHandler(ResiliencePolicies.GetCircuitBreakerPolicy("users-service"))
            .AddPolicyHandler(ResiliencePolicies.GetRetryWithJitterPolicy("users-service"));
        }

        public static void RegisterDataAccessServices(this IServiceCollection serviceCollection, IConfiguration configuration)
        {
            // Resiliencia del acceso a la BD: timeout corto + circuit breaker por entidad + retry con equal jitter.
            // Ver Backend/RESILIENCIA_BD.md.
            var resilience = DbResilienceOptions.FromConfiguration(key => configuration[key]);
            serviceCollection.AddDbResilience(resilience, sp => new MetricsDbResilienceObserver(sp.GetRequiredService<ILoggerFactory>()));

            serviceCollection.AddResilientRepository<User, UsersRepository>();
            serviceCollection.AddResilientRepository<Session, SessionRepository>();
            serviceCollection.AddResilientRepository<StockRequest, StockRequestRepository>();
            serviceCollection.AddResilientRepository<Pharmacy, PharmacyRepository>();
            serviceCollection.AddResilientRepository<UnitMeasure, UnitMeasureRepository>();
            serviceCollection.AddResilientRepository<Purchase, PurchasesRepository>();
            serviceCollection.AddResilientRepository<Presentation, PresentationRepository>();
            serviceCollection.AddResilientRepository<Drug, DrugRepository>();
            serviceCollection.AddResilientRepository<PurchaseDetail, PurchasesDetailRepository>();

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
                    w.Ignore(CoreEventId.LazyLoadOnDisposedContextWarning);
                });
            });

            serviceCollection.AddSingleton<ICustomMetrics, CustomMetrics>();
            serviceCollection.AddSingleton<IStructuredLogger, StructuredLogger>();
        }
    }
}
