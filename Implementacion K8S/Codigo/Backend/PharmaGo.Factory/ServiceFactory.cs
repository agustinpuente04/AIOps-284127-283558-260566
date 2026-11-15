using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PharmaGo.BusinessLogic;
using PharmaGo.DataAccess;
using PharmaGo.DataAccess.Repositories;
using PharmaGo.Domain.Entities;
using PharmaGo.IBusinessLogic;
using PharmaGo.IDataAccess;
using Microsoft.Extensions.Hosting;
using InstrumentationInterface;
using Instrumentation;


namespace PharmaGo.Factory
{
    public static class ServiceFactory
    {

        public static void RegisterBusinessLogicServices(this IServiceCollection serviceCollection, IConfiguration configuration)
        {
            serviceCollection.AddScoped<ILoginManager, LoginManager>();
            serviceCollection.AddScoped<IUsersManager, UsersManager>();
            serviceCollection.AddScoped<IInvitationManager, InvitationManager>();
            serviceCollection.AddScoped<IStockRequestManager, StockRequestManager>();
            serviceCollection.AddScoped<IPurchasesManager, PurchasesManager>();
            serviceCollection.AddScoped<IPharmacyManager, PharmacyManager>();
            serviceCollection.AddScoped<IDrugManager, DrugManager>();
            serviceCollection.AddScoped<IPresentationManager, PresentationManager>();
            serviceCollection.AddScoped<IUnitMeasureManager, UnitMeasureManager>();
            serviceCollection.AddScoped<IExportManager, ExportManager>();
            serviceCollection.AddScoped<IRoleManager, RoleManager>();
        }

        public static void RegisterDataAccessServices(this IServiceCollection serviceCollection, IConfiguration configuration)
        {
            serviceCollection.AddScoped<IRepository<User>, UsersRepository>();
            serviceCollection.AddScoped<IRepository<Session>, SessionRepository>();
            serviceCollection.AddScoped<IRepository<Invitation>, InvitationRepository>();
            serviceCollection.AddScoped<IRepository<Role>, RoleRepository>();
            serviceCollection.AddScoped<IRepository<StockRequest>, StockRequestRepository>();
            serviceCollection.AddScoped<IRepository<Pharmacy>, PharmacyRepository>();
            serviceCollection.AddScoped<IRepository<UnitMeasure>, UnitMeasureRepository>();
            serviceCollection.AddScoped<IRepository<Purchase>, PurchasesRepository>();
            serviceCollection.AddScoped<IRepository<Presentation>, PresentationRepository>();
            serviceCollection.AddScoped<IRepository<Drug>, DrugRepository>();
            serviceCollection.AddScoped<IRepository<PurchaseDetail>, PurchasesDetailRepository>();
            serviceCollection.AddScoped<IRepository<Role>, RoleRepository>();

            serviceCollection.AddDbContext<DbContext, PharmacyGoDbContext>(options =>
            {
                options.UseSqlServer(
                    configuration.GetConnectionString("DefaultConnection"),
                    sqlOptions => { sqlOptions.MigrationsAssembly("PharmaGo.DataAccess"); });
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
                dbContext.Database.Migrate();
            }

            return host;
        }

    }
}