using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PharmaGo.PharmacyService.BusinessLogic;
using PharmaGo.DataAccess;
using PharmaGo.DataAccess.Repositories;
using PharmaGo.Domain.Entities;
using PharmaGo.PharmacyService.IBusinessLogic;
using PharmaGo.IDataAccess;
using Microsoft.Extensions.Hosting;
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
            serviceCollection.AddScoped<IRepository<User>, UsersRepository>();
            serviceCollection.AddScoped<IRepository<Session>, SessionRepository>();
            serviceCollection.AddScoped<IRepository<StockRequest>, StockRequestRepository>();
            serviceCollection.AddScoped<IRepository<Pharmacy>, PharmacyRepository>();
            serviceCollection.AddScoped<IRepository<UnitMeasure>, UnitMeasureRepository>();
            serviceCollection.AddScoped<IRepository<Purchase>, PurchasesRepository>();
            serviceCollection.AddScoped<IRepository<Presentation>, PresentationRepository>();
            serviceCollection.AddScoped<IRepository<Drug>, DrugRepository>();
            serviceCollection.AddScoped<IRepository<PurchaseDetail>, PurchasesDetailRepository>();

            serviceCollection.AddDbContext<DbContext, PharmacyGoDbContext>(options =>
            {
                options.UseSqlServer(
                    configuration.GetConnectionString("DefaultConnection"),
                    sqlOptions => { sqlOptions.MigrationsAssembly("PharmaGo.DataAccess"); });
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
