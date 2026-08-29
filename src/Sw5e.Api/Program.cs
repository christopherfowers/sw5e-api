using Sw5e.Api.Features.Health;
using Sw5e.Api.Security;

var builder = WebApplication.CreateBuilder(args);

// Suppress the server identity banner; it offers attackers free reconnaissance.
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseSw5eSecurityHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.MapHealthEndpoints();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.Run();

// Exposed so that WebApplicationFactory<Program> can host the app in tests.
// This MUST stay in the global namespace: top-level statements emit their
// generated Program class there, and wrapping this declaration in a namespace
// would declare a different, unrelated type that never merges with it.
public partial class Program;
