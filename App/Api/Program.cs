using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Secrets;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddHttpContextAccessor(); // AuditLogger needs the request's source IP

builder.Services.AddBlueTrackAuthentication();

// Windows Integrated Authentication to SQL Server (D-30) -- no SQL login,
// no standing secret. See appsettings.json for the connection string shape.
builder.Services.AddSingleton<IDbConnectionFactory, SqlConnectionFactory>();
builder.Services.AddScoped<IdentityProviderRepository>();
builder.Services.AddScoped<AppUserRepository>();
builder.Services.AddScoped<AccountProgressRepository>();
builder.Services.AddScoped<ReportsRepository>();
builder.Services.AddScoped<AuthorizationRepository>();
builder.Services.AddScoped<NegotiateProviderResolver>();
builder.Services.AddScoped<UserRightsResolver>();
builder.Services.AddScoped<CurrentUserResolver>();
builder.Services.AddScoped<RiskExceptionRepository>();
builder.Services.AddScoped<ApplicationRepository>();
builder.Services.AddScoped<AuditLogger>();
builder.Services.AddScoped<FieldMetadataRepository>();
builder.Services.AddScoped<AppConfigRepository>();
builder.Services.AddScoped<RoleRepository>();
builder.Services.AddScoped<GroupRoleMappingRepository>();
builder.Services.AddScoped<SecretsStoreRepository>();
builder.Services.AddScoped<AuditRepository>();
builder.Services.AddScoped<ReferenceDataRepository>();
builder.Services.AddScoped<AccountProgressLockRepository>();

// Secrets Storage (D-16/D-32/D-79/D-80). Vault-lookup backends (CyberArk
// CP/CCP so far) register as IVaultSecretProvider -- Scoped, not Singleton,
// since they depend on SecretsStoreRepository (Scoped, per the
// SqlConnectionFactory pattern every other repository here follows).
// VaultSecretProviderResolver picks whichever one matches the currently-
// active web.secrets_store row. Windows DPAPI doesn't fit that shape at
// all (D-79) -- it registers as the separate ILocalSecretProtector instead.
builder.Services.AddScoped<IVaultSecretProvider, CyberArkCpSecretsProvider>();
builder.Services.AddHttpClient(nameof(CyberArkCcpSecretsProvider));
builder.Services.AddScoped<IVaultSecretProvider, CyberArkCcpSecretsProvider>();
builder.Services.AddScoped<VaultSecretProviderResolver>();
builder.Services.AddSingleton<ILocalSecretProtector, WindowsDpapiProtector>();

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
