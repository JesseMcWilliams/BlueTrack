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
builder.Services.AddScoped<ReportsRepository>();
builder.Services.AddScoped<AuthorizationRepository>();
builder.Services.AddScoped<UserRightsResolver>();
builder.Services.AddScoped<CurrentUserResolver>();
builder.Services.AddScoped<RiskExceptionRepository>();
builder.Services.AddScoped<ApplicationRepository>();

// One authorization policy per permission (D-05/D-61) -- see
// AuthorizationExtensions for how [Authorize(Policy = Permissions.X)] maps
// onto the "permission" claim PermissionClaimsTransformation adds.
builder.Services.AddBlueTrackAuthorization();

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
