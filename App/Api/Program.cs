using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddBlueTrackAuthentication();

// Windows Integrated Authentication to SQL Server (D-30) -- no SQL login,
// no standing secret. See appsettings.json for the connection string shape.
builder.Services.AddSingleton<IDbConnectionFactory, SqlConnectionFactory>();
builder.Services.AddScoped<IdentityProviderRepository>();
builder.Services.AddScoped<AppUserRepository>();
builder.Services.AddScoped<AccountProgressRepository>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
