/* ============================================================================
   16_BlueTrack_AccountProgressFieldMetadataSeed.sql

   RUN THIS AFTER 01-15.

   Seeds web.account_progress_field_metadata (Design_Interface_Extensibility.md)
   with one row per editable dbo.fact_account_progress column, so the
   Account Progress edit form has something to actually render -- this is
   the "consuming side" of the field-metadata-driven pattern the design doc
   describes, built alongside App/Api/Controllers/AccountProgressController.cs's
   new GetDetail/Update actions.

   NOT included here: ExceptionKey. It's populated by the Risk Exception
   workflow (linking/creating an exception), not hand-typed on this form --
   see Design_Risk_Exception_Tracking.md's own note that this wiring isn't
   built yet.

   FieldType uses the design doc's own controlled-list examples (Text,
   Date, Dropdown, Number) plus one self-evident addition, TextArea, for
   Notes -- the doc's list is explicitly "Text / Date / Dropdown / Number,
   etc.", not exhaustive.

   Guarded -- safe to re-run (only inserts rows for FieldNames not already present).
   ============================================================================ */

USE $DatabaseName$;
GO

INSERT INTO web.account_progress_field_metadata
    (FieldName, DisplayLabel, FieldType, ReferenceTable, IsRequired, RequiredPermission, DisplayOrder)
SELECT v.FieldName, v.DisplayLabel, v.FieldType, v.ReferenceTable, v.IsRequired, NULL, v.DisplayOrder
FROM (VALUES
    ('CurrentStageKey',        'Blueprint Stage',        'Dropdown', 'dim_blueprint_stage',   1, 10),
    ('CurrentStatusKey',       'Status',                  'Dropdown', 'dim_progress_status',   1, 20),
    ('RiskLevelKey',           'Risk Level',               'Dropdown', 'dim_risk_level',        0, 30),
    ('AccountTypeKey',         'Account Type',              'Dropdown', 'dim_account_type',      0, 40),
    ('SORKey',                 'Source of Record',           'Dropdown', 'dim_source_of_record',  0, 50),
    ('OwnerName',              'Owner Name',                   'Text',      NULL,                    0, 60),
    ('BusinessUnit',           'Business Unit',                 'Text',      NULL,                    0, 70),
    ('TargetRemediationDate',  'Target Remediation Date',        'Date',      NULL,                    0, 80),
    ('ActualCompletionDate',   'Actual Completion Date',           'Date',      NULL,                    0, 90),
    ('Notes',                  'Notes',                              'TextArea',  NULL,                    0, 100)
) AS v(FieldName, DisplayLabel, FieldType, ReferenceTable, IsRequired, DisplayOrder)
WHERE NOT EXISTS (
    SELECT 1 FROM web.account_progress_field_metadata fm WHERE fm.FieldName = v.FieldName
);

PRINT 'account_progress_field_metadata seeded with Account Progress editable fields.';
