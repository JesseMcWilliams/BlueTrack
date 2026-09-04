using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Secrets;
using ITfoxtec.Identity.Saml2.MvcCore.Configuration;
using ITfoxtec.Identity.Saml2.Schemas;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddHttpContextAccessor(); // AuditLogger needs the request's source IP

builder.Services.AddBlueTrackAuthentication(builder.Configuration);

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

// D-13/D-82: cached rights per identity, backed by
// Microsoft.Extensions.Caching.SqlServer (web.distributed_cache) -- see
// UserRightsCache's own comment on why this isn't an ASP.NET Core
// cookie-based Session.
builder.Services.AddDistributedSqlServerCache(options =>
{
    options.ConnectionString = builder.Configuration.GetConnectionString("BlueTrackDb")
        ?? throw new InvalidOperationException("Missing ConnectionStrings:BlueTrackDb in configuration.");
    options.SchemaName = "web";
    options.TableName = "distributed_cache";
});
builder.Services.AddScoped<UserRightsCache>();

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

// D-84: Azure Key Vault, AWS Secrets Manager, and CyberArk Conjur -- built
// as a placeholder framework ahead of real connection details (structurally
// real SDK/REST calls, unverified against a live service). Each reads its
// own settings from web.secrets_store.BackendSettings and, where it needs
// a credential to authenticate to its own remote service, decrypts it via
// ILocalSecretProtector -- a provider bootstrapping its own credential
// can't recursively depend on "the active secrets store."
builder.Services.AddScoped<IVaultSecretProvider, AzureKeyVaultSecretsProvider>();
builder.Services.AddScoped<IVaultSecretProvider, AwsSecretsManagerSecretsProvider>();
builder.Services.AddHttpClient(nameof(CyberArkConjurSecretsProvider));
builder.Services.AddScoped<IVaultSecretProvider, CyberArkConjurSecretsProvider>();

// SAML (D-84/D-23): ITfoxtec.Identity.Saml2.MvcCore's own cookie-based
// session for the SAML sign-in flow (Saml2Controller) -- a separate,
// dedicated scheme (Saml2Constants.AuthenticationScheme, "saml2") from
// OIDC's Cookies scheme (AuthenticationExtensions.cs), since the two
// libraries manage their own sign-in independently. SameSite=None +
// Secure=Always because the IdP's POST back to our ACS endpoint (D-25's
// SAML Security Hardening) is a cross-site request.
builder.Services.AddSaml2(
    loginPath: "/api/auth/saml/login",
    slidingExpiration: true,
    accessDeniedPath: "/api/auth/saml/login",
    sessionStore: null,
    cookieSameSite: SameSiteMode.None,
    cookieDomain: null,
    cookieSecurePolicy: CookieSecurePolicy.Always,
    cookieName: "BlueTrack.Saml2");

// Confirmed by testing (D-84): AddSaml2() resets AuthenticationOptions'
// Default(Challenge)Scheme to its own "saml2" cookie scheme, which broke
// Windows Integrated auth entirely (a bare 401 with no WWW-Authenticate
// header, instead of the Negotiate challenge round trip). Forcing it back
// to Negotiate here, after AddSaml2(), restores the original default --
// SAML/OIDC are only ever reached via an explicit Challenge(..., "OIDC")
// or Saml2Controller's own redirect, never as the app's automatic default.
builder.Services.Configure<Microsoft.AspNetCore.Authentication.AuthenticationOptions>(options =>
{
    options.DefaultScheme = Microsoft.AspNetCore.Authentication.Negotiate.NegotiateDefaults.AuthenticationScheme;
    options.DefaultAuthenticateScheme = Microsoft.AspNetCore.Authentication.Negotiate.NegotiateDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = Microsoft.AspNetCore.Authentication.Negotiate.NegotiateDefaults.AuthenticationScheme;
});

builder.Services.AddScoped<Saml2ConfigurationFactory>();

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

// D-84: OIDC/SAML fallback -- Negotiate is still the app's actual default
// scheme (AuthenticationExtensions.cs's own comment explains why a policy
// scheme broke Kestrel's Negotiate integration when tried). For a request
// Negotiate didn't authenticate, check for either cookie-based session
// (OIDC's Cookies scheme, or SAML's own "saml2" scheme via ITfoxtec) and
// adopt whichever one succeeds -- so [Authorize] downstream sees an
// authenticated user without every controller needing to list every
// scheme explicitly.
app.Use(async (context, next) =>
{
    if (context.User.Identity is not { IsAuthenticated: true })
    {
        foreach (var scheme in new[] { CookieAuthenticationDefaults.AuthenticationScheme, Saml2Constants.AuthenticationScheme })
        {
            var result = await context.AuthenticateAsync(scheme);
            if (result.Succeeded && result.Principal is not null)
            {
                context.User = result.Principal;
                break;
            }
        }
    }

    await next(context);
});

app.UseSaml2();
app.UseAuthorization();

app.MapControllers();

app.Run();
