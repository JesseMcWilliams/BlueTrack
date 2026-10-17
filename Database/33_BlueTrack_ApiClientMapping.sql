/* ============================================================================
   33_BlueTrack_ApiClientMapping.sql

   RUN THIS AFTER 01-32. Guarded -- safe to re-run.

   D-243 (phase 2): a caller can exchange a token from an identity provider
   (an OIDC provider on Admin > Identity Providers) for a BlueTrack API token.

   web.api_client_mapping says which role such a caller gets, by either:
     ClientId  the token's client (the azp or client_id claim), for a
               client-credentials caller such as a script's own application;
     Group     a value in the token's groups claim (the claim named on the
               provider), for a token issued to a person or a group.
   One role per mapping and never Admin (checked by the API). When several
   mappings match a token, the one with the lowest DisplayOrder wins, then
   ClientId before Group. Not the person Group -> Role Mapping: a script gets
   only what is mapped here.

   Also the ApiClientMappingChanged audit event type.
   ============================================================================ */

USE $DatabaseName$;
GO

IF OBJECT_ID('web.api_client_mapping', 'U') IS NULL
BEGIN
    CREATE TABLE web.api_client_mapping (
        ApiClientMappingKey INT IDENTITY(1,1) CONSTRAINT PK_api_client_mapping PRIMARY KEY,
        ProviderKey         INT            NOT NULL CONSTRAINT FK_api_client_mapping_Provider REFERENCES web.identity_provider_config(ProviderKey),
        MatchType           NVARCHAR(20)   NOT NULL CONSTRAINT CK_api_client_mapping_MatchType CHECK (MatchType IN ('ClientId', 'Group')),
        MatchValue          NVARCHAR(300)  NOT NULL,
        AppRoleKey          INT            NOT NULL CONSTRAINT FK_api_client_mapping_AppRole REFERENCES web.app_role(AppRoleKey),
        DisplayName         NVARCHAR(100)  NULL,
        DisplayOrder        INT            NOT NULL CONSTRAINT DF_api_client_mapping_DisplayOrder DEFAULT (100),
        IsEnabled           BIT            NOT NULL CONSTRAINT DF_api_client_mapping_IsEnabled DEFAULT (1),
        CreatedBy           INT            NOT NULL CONSTRAINT FK_api_client_mapping_CreatedBy REFERENCES web.app_user(UserKey),
        CreatedAt           DATETIME2      NOT NULL CONSTRAINT DF_api_client_mapping_CreatedAt DEFAULT SYSUTCDATETIME(),
        LastUsedAt          DATETIME2      NULL,
        CONSTRAINT UQ_api_client_mapping UNIQUE (ProviderKey, MatchType, MatchValue)
    );
END
GO

INSERT INTO web.dim_audit_event_type (EventTypeName, Description)
SELECT 'ApiClientMappingChanged', 'An API client mapping (identity provider token to role) was created, enabled, disabled or deleted'
WHERE NOT EXISTS (SELECT 1 FROM web.dim_audit_event_type WHERE EventTypeName = 'ApiClientMappingChanged');
GO
