using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using PaymentOrchestration.Api;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Payments")
    ?? "Data Source=payment-orchestration.db";
builder.Services.AddDbContext<PaymentDbContext>(options => options.UseSqlite(connectionString));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
    options.SwaggerDoc("v1", new()
    {
        Title = "Payment Orchestration API",
        Version = "v1",
        Description = "Tokenized payment initiation, status tracking, audit history, and provider webhooks."
    }));
builder.Services.Configure<PaymentProcessingOptions>(
    builder.Configuration.GetSection("PaymentProcessing"));
builder.Services.Configure<PaymentGatewayOptions>(
    builder.Configuration.GetSection("PaymentGateway"));
builder.Services.Configure<PaymentWebhookOptions>(
    builder.Configuration.GetSection("PaymentWebhook"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IPaymentProvider, SandboxSimulationProvider>();
builder.Services.AddSingleton<IPaymentProviderResolver, PaymentProviderRegistry>();

builder.Services.AddScoped<PaymentService>();
builder.Services.AddScoped<WebhookService>();
builder.Services.AddSingleton<WebhookSignatureValidator>();
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

await using (var scope = app.Services.CreateAsyncScope())
{
    var database = scope.ServiceProvider.GetRequiredService<PaymentDbContext>();
    await database.Database.EnsureCreatedAsync();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .WithName("GetHealth")
    .WithTags("Health")
    .WithSummary("Check API health")
    .Produces(StatusCodes.Status200OK);
app.MapGet("/api/providers", (IPaymentProviderResolver providers) => Results.Ok(providers.GetNames()))
    .WithName("ListPaymentProviders")
    .WithTags("Payments")
    .WithSummary("List configured payment providers")
    .Produces<string[]>(StatusCodes.Status200OK);
app.MapPaymentEndpoints();

app.Run();

public partial class Program;
