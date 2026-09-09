/* ============================================================================
   21_BlueTrack_RiskScoringImportSchema.sql

   RUN THIS AFTER 01-20. Guarded -- safe to re-run.

   Design_Risk_Scoring.md (D-101-105, D-119) Phase B: the target-match
   review queue and the configurable import field-mapping layer. Both were
   scoped for this phase specifically -- the review queue only gets rows
   once real CSV/import matching starts happening (Phase B), and the
   mapping profiles exist to serve that same import path.
   ============================================================================ */

USE $DatabaseName$;
GO

-- web.target_match_review: a weak (IP-only, etc.) match an analyst needs to
-- confirm rather than one this app silently merges or splits.
IF OBJECT_ID('web.target_match_review', 'U') IS NULL
BEGIN
    CREATE TABLE web.target_match_review (
        TargetMatchReviewKey  INT IDENTITY(1,1) PRIMARY KEY,
        CandidateTargetKey       INT              NULL REFERENCES web.dim_target(TargetKey),
        IdentifierType              NVARCHAR(50)     NOT NULL REFERENCES web.dim_target_identifier_type(IdentifierType),
        IdentifierValue                NVARCHAR(300)    NOT NULL,
        ImportBatchId                     UNIQUEIDENTIFIER NULL,
        SourceFileName                       NVARCHAR(260)    NULL,
        CreatedDate                             DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        ResolvedBy                                 INT              NULL REFERENCES web.app_user(UserKey),
        ResolvedDate                                  DATETIME2        NULL,
        Resolution                                       NVARCHAR(20)     NULL,
        CONSTRAINT CK_target_match_review_Resolution CHECK (Resolution IS NULL OR Resolution IN ('Merged', 'NewTarget', 'Ignored'))
    );
END
GO

-- web.import_mapping_profile: one named mapping per distinct source file
-- shape. More than one profile can exist per FeedType.
IF OBJECT_ID('web.import_mapping_profile', 'U') IS NULL
BEGIN
    CREATE TABLE web.import_mapping_profile (
        ImportMappingProfileKey INT IDENTITY(1,1) PRIMARY KEY,
        FeedType                   NVARCHAR(50)     NOT NULL,
        ProfileName                   NVARCHAR(200)    NOT NULL,
        IsActive                         BIT              NOT NULL DEFAULT 1,
        Description                         NVARCHAR(1000)   NULL,
        CreatedBy                             INT              NULL REFERENCES web.app_user(UserKey),
        ModifiedBy                               INT              NULL REFERENCES web.app_user(UserKey),
        ModifiedDate                                DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_import_mapping_profile UNIQUE (FeedType, ProfileName),
        CONSTRAINT CK_import_mapping_profile_FeedType CHECK (FeedType IN ('TargetInventory', 'AccessGroupInventory', 'AccessGroupTargetMap', 'AccountAccessGroupMembership', 'AccountTargetMap'))
    );
END
GO

-- web.import_mapping_field: per profile, which source column feeds which
-- internal field.
IF OBJECT_ID('web.import_mapping_field', 'U') IS NULL
BEGIN
    CREATE TABLE web.import_mapping_field (
        ImportMappingFieldKey   INT IDENTITY(1,1) PRIMARY KEY,
        ImportMappingProfileKey    INT              NOT NULL REFERENCES web.import_mapping_profile(ImportMappingProfileKey),
        SourceColumnName              NVARCHAR(200)    NOT NULL,
        TargetFieldName                  NVARCHAR(100)    NOT NULL,
        IsRequired                          BIT              NOT NULL DEFAULT 0,
        DefaultValue                           NVARCHAR(300)    NULL,
        CONSTRAINT UQ_import_mapping_field UNIQUE (ImportMappingProfileKey, TargetFieldName)
    );
END
GO

PRINT '21_BlueTrack_RiskScoringImportSchema.sql complete.';
