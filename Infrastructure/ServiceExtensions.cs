using Application.Consumers;
using Application.IRepositories;
using Application.IServices;
using Application.IServices.IConsumers;
using Application.Options;
using Application.Services.Queue;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Repositories;
using Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Prypto.ServiceBusHelpers;
using Prypto.ServiceBusHelpers.Consumer;
using Prypto.ServiceBusHelpers.Options;

namespace Infrastructure
{
    /// <summary>
    /// DI wiring, mirroring InstaMortgageService's ServiceExtensions so the registrations lift
    /// across with the code. Trimmed to this integration: outbox + queue + adapter client.
    /// </summary>
    public static class ServiceExtensions
    {
        /// <summary>Bind options from configuration (Service Bus connection + this integration's settings).</summary>
        public static IServiceCollection AddHubSpotSyncOptions(this IServiceCollection services, IConfiguration configuration)
        {
            services.AddOptions<ServiceBusSettings>().Bind(configuration.GetSection("ServiceBusSettings"));
            services.AddOptions<HubSpotSyncSettings>().Bind(configuration.GetSection(HubSpotSyncSettings.SectionName));
            return services;
        }

        /// <summary>Register the AppDbContext on PostgreSQL (Npgsql), same as InstaMortgageService.</summary>
        public static IServiceCollection ConfigureDatabase(this IServiceCollection services, IConfiguration configuration)
        {
            var connectionString = configuration.GetConnectionString("HubSpotSyncConnection");
            services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
            return services;
        }

        /// <summary>Register services, the producer, the consumer, and the adapter HttpClient.</summary>
        public static IServiceCollection AddHubSpotSyncServices(this IServiceCollection services)
        {
            services.AddScoped<IOutboxRepository, OutboxRepository>();
            services.AddScoped<IInboundLeadRepository, InboundLeadRepository>();

            // Service Bus helpers (Prypto)
            services.AddSingleton<IQueueCheckService, QueueCheckService>();
            services.AddTransient<IQueueMessageProducer, QueueMessageProducer>();
            services.AddSingleton<IHubSpotConsumer, HubSpotConsumer>();

            // Outbound call to the adapter Function (typed HttpClient)
            services.AddHttpClient<IHubSpotAdapterClient, HubSpotAdapterClient>();

            return services;
        }

        /// <summary>
        /// Start the Service Bus message handler outside local dev (matches InstaMortgageService:
        /// consumers only attach in non-local environments).
        /// </summary>
        public static void RegisterMessageConsumers(this IServiceProvider serviceProvider)
        {
            var environment = serviceProvider.GetRequiredService<IHostEnvironment>();
            if (environment.EnvironmentName.Equals("local", StringComparison.OrdinalIgnoreCase))
                return;

            var consumer = (BaseConsumer)serviceProvider.GetRequiredService<IHubSpotConsumer>();
            consumer.RegisterOnMessageHandlerAndReceiveMessagesAsync().GetAwaiter().GetResult();
        }
    }
}
