/* ============================================================================
   81_BlueTrack_RiskFindingsPrivilegedLookup.sql

   RUN THIS AFTER 01-80. Safe to re-run (CREATE OR ALTER).

   D-370 (the user, 2026-11-18, from a customer's resource report):
   web.usp_DeriveRiskFindings as in 72, with one statement rewritten. Marking
   a hard-coded credential privileged compared it with every DNA account row,
   working out each row's user and machine names (CHARINDEX, SUBSTRING,
   COLLATE) inside a correlated EXISTS with ORs, so SQL Server could only
   loop: 48 seconds of CPU for one run at the customer. The privileged DNA
   rows' names are now worked out once into an indexed temporary table and
   looked up by user name. The rules are unchanged.
   ============================================================================ */
USE $DatabaseName$;
GO

/* web.usp_DeriveRiskFindings: as in 72_BlueTrack_RiskDefaultsAndFindings.sql, with the privileged lookup above (D-370). */
CREATE OR ALTER PROCEDURE web.usp_DeriveRiskFindings
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @BeforeCount INT, @BeforeSum INT;
    SELECT @BeforeCount = COUNT(*), @BeforeSum = CHECKSUM_AGG(CHECKSUM(FindingType, TargetKey, AccountKey)) FROM web.risk_finding;

    IF OBJECT_ID('tempdb..#hcc') IS NOT NULL DROP TABLE #hcc;
    CREATE TABLE #hcc (
        DnaHardCodedKey INT           NOT NULL PRIMARY KEY,
        Machine         NVARCHAR(300) COLLATE Latin1_General_CI_AS NULL,
        MachineShort    NVARCHAR(300) COLLATE Latin1_General_CI_AS NULL,
        UserName        NVARCHAR(300) COLLATE Latin1_General_CI_AS NULL,
        TargetAddress   NVARCHAR(300) COLLATE Latin1_General_CI_AS NULL,
        TargetShort     NVARCHAR(300) COLLATE Latin1_General_CI_AS NULL,
        IsPrivileged    BIT           NOT NULL DEFAULT 0
    );

    INSERT INTO #hcc (DnaHardCodedKey, Machine, MachineShort, UserName, TargetAddress, TargetShort)
    SELECT h.DnaHardCodedKey, m.Machine, LEFT(m.Machine, CHARINDEX(N'.', m.Machine + N'.') - 1),
           u.UserName, a.TargetAddress, LEFT(a.TargetAddress, CHARINDEX(N'.', a.TargetAddress + N'.') - 1)
    FROM web.dna_hard_coded_credential h
    CROSS APPLY (SELECT NULLIF(NULLIF(LTRIM(RTRIM(h.MachineName)), N''), N'N/A') AS Machine) m
    CROSS APPLY (SELECT NULLIF(NULLIF(LTRIM(RTRIM(h.AccountName)), N''), N'N/A') AS RawUser,
                        NULLIF(NULLIF(LTRIM(RTRIM(h.TargetSystemAddress)), N''), N'N/A') AS RawAddress) r
    CROSS APPLY (SELECT CASE WHEN CHARINDEX(N'\', r.RawUser) > 0 THEN SUBSTRING(r.RawUser, CHARINDEX(N'\', r.RawUser) + 1, 300) ELSE r.RawUser END AS UserName) u
    -- One colon is a port (SAIATEST:446); more is an IPv6 address, kept whole.
    CROSS APPLY (SELECT CASE WHEN LEN(r.RawAddress) - LEN(REPLACE(r.RawAddress, N':', N'')) = 1
                             THEN LEFT(r.RawAddress, CHARINDEX(N':', r.RawAddress) - 1) ELSE r.RawAddress END AS TargetAddress) a;

    -- The BlueTrack accounts each credential names.
    IF OBJECT_ID('tempdb..#hccAccount') IS NOT NULL DROP TABLE #hccAccount;
    SELECT DISTINCT h.DnaHardCodedKey, fa.AccountKey
    INTO #hccAccount
    FROM #hcc h
    JOIN dbo.fact_account fa
      ON fa.IsDeleted = 0 AND h.UserName IS NOT NULL AND h.TargetAddress IS NOT NULL
     AND fa.UserName COLLATE Latin1_General_CI_AS = h.UserName
     AND (   fa.Address COLLATE Latin1_General_CI_AS = h.TargetAddress
          OR (CHARINDEX(N'.', fa.Address) = 0 AND fa.Address COLLATE Latin1_General_CI_AS = h.TargetShort)
          OR (CHARINDEX(N'.', h.TargetAddress) = 0 AND LEFT(fa.Address, CHARINDEX(N'.', fa.Address + N'.') - 1) COLLATE Latin1_General_CI_AS = h.TargetAddress));

    -- Privileged: DNA says so for that user on the target system or on the machine, or the account's AD account is Admin.
    -- D-370: the privileged DNA users' names are worked out once, into an indexed table, rather than for every pair of
    -- credential and DNA row (48 s of CPU at a customer). Same rules as in 72.
    IF OBJECT_ID('tempdb..#dnaPrivileged') IS NOT NULL DROP TABLE #dnaPrivileged;
    CREATE TABLE #dnaPrivileged (
        UserName     NVARCHAR(300) COLLATE Latin1_General_CI_AS NOT NULL,
        Machine      NVARCHAR(300) COLLATE Latin1_General_CI_AS NULL,
        MachineShort NVARCHAR(300) COLLATE Latin1_General_CI_AS NULL,
        MachineDot   INT           NULL   -- CHARINDEX('.', MachineName): 0 for a short name, NULL for none
    );
    INSERT INTO #dnaPrivileged (UserName, Machine, MachineShort, MachineDot)
    SELECT DISTINCT k.UserName, d.MachineName, k.MachineShort, CHARINDEX(N'.', d.MachineName)
    FROM web.dna_account d
    CROSS APPLY (SELECT CASE WHEN CHARINDEX(N'\', d.AccountName) > 0 THEN SUBSTRING(d.AccountName, CHARINDEX(N'\', d.AccountName) + 1, 300) ELSE d.AccountName END AS UserName,
                        LEFT(d.MachineName, CHARINDEX(N'.', d.MachineName + N'.') - 1) AS MachineShort) k
    WHERE (d.AccountCategory LIKE N'Privileged%' OR NULLIF(LTRIM(RTRIM(d.PrivilegedDomainGroup)), N'') IS NOT NULL)
      AND k.UserName IS NOT NULL;
    CREATE INDEX IX_dnaPrivileged_UserName ON #dnaPrivileged (UserName) INCLUDE (Machine, MachineShort, MachineDot);

    UPDATE h SET IsPrivileged = 1
    FROM #hcc h
    WHERE EXISTS (
            SELECT 1 FROM #dnaPrivileged d
            WHERE h.UserName IS NOT NULL
              AND d.UserName = h.UserName
              AND (   d.Machine IN (h.TargetAddress, h.Machine)
                   OR d.MachineShort IN (h.TargetShort, h.MachineShort)
                      AND (d.MachineDot = 0 OR CHARINDEX(N'.', h.TargetAddress) = 0 OR CHARINDEX(N'.', h.Machine) = 0)))
       OR EXISTS (
            SELECT 1 FROM #hccAccount ha
            JOIN web.discovered_account da ON da.ResolvedAccountKey = ha.AccountKey
            WHERE ha.DnaHardCodedKey = h.DnaHardCodedKey AND COALESCE(da.ClassOverride, da.AccountClass) = N'Admin');

    -- Each target's names: its own and its identifiers.
    IF OBJECT_ID('tempdb..#targetName') IS NOT NULL DROP TABLE #targetName;
    SELECT DISTINCT n.TargetKey, n.Name COLLATE Latin1_General_CI_AS AS Name,
           LEFT(n.Name, CHARINDEX(N'.', n.Name + N'.') - 1) COLLATE Latin1_General_CI_AS AS NameShort
    INTO #targetName
    FROM (SELECT TargetKey, LTRIM(RTRIM(TargetName)) AS Name FROM web.dim_target
          UNION SELECT TargetKey, LTRIM(RTRIM(IdentifierValue)) FROM web.target_identifier) n
    WHERE n.Name <> N'';

    BEGIN TRANSACTION;

    DELETE FROM web.risk_finding;

    -- The system the credential was found on.
    INSERT INTO web.risk_finding (FindingType, TargetKey, DnaHardCodedKey)
    SELECT DISTINCT CASE WHEN h.IsPrivileged = 1 THEN N'DnaHardCodedPrivilegedSecret' ELSE N'DnaHardCodedSecret' END, tn.TargetKey, h.DnaHardCodedKey
    FROM #hcc h
    JOIN #targetName tn
      ON h.Machine IS NOT NULL
     AND (   tn.Name = h.Machine
          OR (CHARINDEX(N'.', tn.Name) = 0 AND tn.Name = h.MachineShort)
          OR (CHARINDEX(N'.', h.Machine) = 0 AND tn.NameShort = h.Machine));

    -- The account it names.
    INSERT INTO web.risk_finding (FindingType, AccountKey, DnaHardCodedKey)
    SELECT CASE WHEN h.IsPrivileged = 1 THEN N'DnaHardCodedPrivilegedSecret' ELSE N'DnaHardCodedSecret' END, ha.AccountKey, h.DnaHardCodedKey
    FROM #hccAccount ha JOIN #hcc h ON h.DnaHardCodedKey = ha.DnaHardCodedKey;

    -- DNA accounts linked to a BlueTrack account (D-346).
    INSERT INTO web.risk_finding (FindingType, AccountKey, DnaAccountKey)
    SELECT N'DnaPassTheHash', d.TrackedAccountKey, d.DnaAccountKey
    FROM web.dna_account d
    JOIN dbo.fact_account fa ON fa.AccountKey = d.TrackedAccountKey AND fa.IsDeleted = 0
    WHERE d.PassTheHashVulnerable = N'Yes';

    INSERT INTO web.risk_finding (FindingType, AccountKey, DnaAccountKey)
    SELECT N'DnaNonCompliant', d.TrackedAccountKey, d.DnaAccountKey
    FROM web.dna_account d
    JOIN dbo.fact_account fa ON fa.AccountKey = d.TrackedAccountKey AND fa.IsDeleted = 0
    WHERE d.ComplianceStatus LIKE N'Non-compliant%';

    -- An account with a finding of its own gets a score row, as usp_DeriveAccountTargetMap_FromPendingSafes does.
    MERGE web.account_risk_score AS tgt
    USING (SELECT DISTINCT AccountKey FROM web.risk_finding WHERE AccountKey IS NOT NULL) AS src (AccountKey)
    ON tgt.AccountKey = src.AccountKey
    WHEN NOT MATCHED THEN INSERT (AccountKey, IsRiskScoreStale) VALUES (src.AccountKey, 1);

    DECLARE @AfterCount INT, @AfterSum INT;
    SELECT @AfterCount = COUNT(*), @AfterSum = CHECKSUM_AGG(CHECKSUM(FindingType, TargetKey, AccountKey)) FROM web.risk_finding;
    IF @AfterCount <> @BeforeCount OR ISNULL(@AfterSum, 0) <> ISNULL(@BeforeSum, 0)
    BEGIN
        UPDATE web.dim_access_group SET IsRiskScoreStale = 1;
        UPDATE web.account_risk_score SET IsRiskScoreStale = 1;
    END

    COMMIT TRANSACTION;

    DROP TABLE #hcc;
    DROP TABLE #hccAccount;
    DROP TABLE #targetName;
END
GO

PRINT '81_BlueTrack_RiskFindingsPrivilegedLookup.sql complete.';
GO
