<#
.SYNOPSIS
    Compares two BlueTrack databases' schemas (and, optionally, their data)
    and lists every difference.

.DESCRIPTION
    D-195. Two uses:
      - Proving the consolidated baseline scripts build exactly what the old
        numbered scripts built: build one database from each and compare them
        with -IncludeData (the seed rows must match too).
      - The drift check: build a reference database from this repo's scripts
        on the same SQL Server, then compare a live database against it to
        find anything a hand-applied script left behind or missed.

    The schema comparison covers schemas, tables, columns (type, size,
    nullability, identity, computed definition, collation, order), defaults,
    CHECK and foreign-key constraints, indexes and keys, procedures, views,
    functions and triggers (definition text with whitespace normalized, plus
    their QUOTED_IDENTIFIER/ANSI_NULLS settings), user-defined types,
    sequences, synonyms, and database roles with their permissions.
    System-generated constraint names are ignored; database users and logins
    are not compared, since they differ by environment by design.

    -IgnoreComments compares procedure, view, function and trigger
    definitions with their comments removed, so a comment edited in a script
    after a database was built isn't reported. Use it for drift checks.

    -IncludeData adds a row count and checksum per table, over every column
    except dates/times, GUIDs, XML and binary data (values that differ on
    every build). Use it only between freshly built databases: a live
    database's data always differs.

    Returns nothing when the databases match; otherwise writes one line per
    difference and exits with code 1.

.EXAMPLE
    .\Compare-BlueTrackSchema.ps1 -SqlServerInstance localhost -ReferenceDatabase BlueTrackRef -DifferenceDatabase BlueTrack -IgnoreComments
.EXAMPLE
    .\Compare-BlueTrackSchema.ps1 -SqlServerInstance localhost -ReferenceDatabase BlueTrackRefOld -DifferenceDatabase BlueTrackRefNew -IncludeData
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$SqlServerInstance,
    [Parameter(Mandatory)] [string]$ReferenceDatabase,
    [Parameter(Mandatory)] [string]$DifferenceDatabase,
    [bool]$UseWindowsAuth = $true,
    [pscredential]$SqlCredential,
    [switch]$IncludeData,
    [switch]$IgnoreComments
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'Modules\BlueTrack.Database.psm1') -Force

if (-not $UseWindowsAuth -and -not $SqlCredential) {
    $SqlCredential = Get-Credential -Message "SQL Server login for $SqlServerInstance"
}

$catalogSql = @'
SET NOCOUNT ON;
WITH cols AS (
    SELECT c.object_id, c.column_id, c.name,
           ROW_NUMBER() OVER (PARTITION BY c.object_id ORDER BY c.column_id) AS Ordinal
    FROM sys.columns c
)
SELECT 'Schema' AS Kind, s.name AS Name, '' AS Detail
FROM sys.schemas s WHERE s.schema_id BETWEEN 5 AND 16383
UNION ALL
SELECT 'Table', SCHEMA_NAME(t.schema_id) + '.' + t.name, ''
FROM sys.tables t WHERE t.is_ms_shipped = 0
UNION ALL
SELECT 'Column', SCHEMA_NAME(o.schema_id) + '.' + o.name + '.' + c.name,
       CONCAT('#', k.Ordinal, ' ', TYPE_NAME(c.user_type_id), '(', c.max_length, ',', c.precision, ',', c.scale, ')',
              ' null=', c.is_nullable,
              ' identity=', IIF(c.is_identity = 1, CONCAT(CAST(ic.seed_value AS NVARCHAR(40)), ',', CAST(ic.increment_value AS NVARCHAR(40))), '-'),
              ' computed=', ISNULL(cc.definition, '-'), ' persisted=', ISNULL(CAST(cc.is_persisted AS NVARCHAR(1)), '-'),
              ' collation=', ISNULL(c.collation_name, '-'))
FROM sys.columns c
JOIN sys.objects o ON o.object_id = c.object_id AND o.type IN ('U', 'TT') AND o.is_ms_shipped = 0
JOIN cols k ON k.object_id = c.object_id AND k.column_id = c.column_id
LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
UNION ALL
SELECT 'Default', SCHEMA_NAME(t.schema_id) + '.' + t.name + '.' + c.name,
       CONCAT(IIF(dc.is_system_named = 1, '', dc.name + ' '), dc.definition)
FROM sys.default_constraints dc
JOIN sys.tables t ON t.object_id = dc.parent_object_id
JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
UNION ALL
SELECT 'Check', SCHEMA_NAME(t.schema_id) + '.' + t.name + ' ' + IIF(ck.is_system_named = 1, 'unnamed: ' + ck.definition, ck.name),
       CONCAT(ck.definition, ' disabled=', ck.is_disabled, ' trusted=', 1 - ck.is_not_trusted)
FROM sys.check_constraints ck
JOIN sys.tables t ON t.object_id = ck.parent_object_id
UNION ALL
SELECT 'ForeignKey', SCHEMA_NAME(t.schema_id) + '.' + t.name + ' ' + IIF(fk.is_system_named = 1, 'unnamed: ' + fkc.Cols, fk.name),
       CONCAT('(', fkc.Cols, ') -> ', SCHEMA_NAME(rt.schema_id), '.', rt.name, '(', fkc.RefCols, ')',
              ' delete=', fk.delete_referential_action_desc COLLATE DATABASE_DEFAULT, ' update=', fk.update_referential_action_desc COLLATE DATABASE_DEFAULT,
              ' disabled=', fk.is_disabled, ' trusted=', 1 - fk.is_not_trusted)
FROM sys.foreign_keys fk
JOIN sys.tables t ON t.object_id = fk.parent_object_id
JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
CROSS APPLY (
    SELECT STRING_AGG(pc.name, ',') WITHIN GROUP (ORDER BY x.constraint_column_id) AS Cols,
           STRING_AGG(rc.name, ',') WITHIN GROUP (ORDER BY x.constraint_column_id) AS RefCols
    FROM sys.foreign_key_columns x
    JOIN sys.columns pc ON pc.object_id = x.parent_object_id AND pc.column_id = x.parent_column_id
    JOIN sys.columns rc ON rc.object_id = x.referenced_object_id AND rc.column_id = x.referenced_column_id
    WHERE x.constraint_object_id = fk.object_id
) fkc
UNION ALL
SELECT 'Index', SCHEMA_NAME(o.schema_id) + '.' + o.name + ' ' +
       CASE WHEN kc.is_system_named = 1 THEN IIF(i.is_primary_key = 1, 'primary key', 'unnamed unique: ' + ixc.KeyCols)
            ELSE i.name END,
       CONCAT(i.type_desc COLLATE DATABASE_DEFAULT, ' unique=', i.is_unique, ' pk=', i.is_primary_key, ' uq=', i.is_unique_constraint,
              ' keys=', ixc.KeyCols, ' include=', ISNULL(ixc.IncludeCols, '-'), ' filter=', ISNULL(i.filter_definition, '-'))
FROM sys.indexes i
JOIN sys.objects o ON o.object_id = i.object_id AND o.type IN ('U', 'V') AND o.is_ms_shipped = 0
LEFT JOIN sys.key_constraints kc ON kc.parent_object_id = i.object_id AND kc.unique_index_id = i.index_id
CROSS APPLY (
    SELECT STRING_AGG(IIF(x.is_included_column = 0, c.name + IIF(x.is_descending_key = 1, ' DESC', ''), NULL), ',') WITHIN GROUP (ORDER BY x.key_ordinal, x.index_column_id) AS KeyCols,
           STRING_AGG(IIF(x.is_included_column = 1, c.name, NULL), ',') WITHIN GROUP (ORDER BY x.key_ordinal, x.index_column_id) AS IncludeCols
    FROM sys.index_columns x
    JOIN sys.columns c ON c.object_id = x.object_id AND c.column_id = x.column_id
    WHERE x.object_id = i.object_id AND x.index_id = i.index_id
) ixc
WHERE i.type > 0
UNION ALL
SELECT 'Module', o.type_desc COLLATE DATABASE_DEFAULT + ' ' + SCHEMA_NAME(o.schema_id) + '.' + o.name,
       CONCAT('quoted_identifier=', m.uses_quoted_identifier, ' ansi_nulls=', m.uses_ansi_nulls, ' ', m.definition)
FROM sys.sql_modules m
JOIN sys.objects o ON o.object_id = m.object_id AND o.is_ms_shipped = 0
UNION ALL
SELECT 'Type', SCHEMA_NAME(ty.schema_id) + '.' + ty.name,
       CONCAT(TYPE_NAME(ty.system_type_id), '(', ty.max_length, ',', ty.precision, ',', ty.scale, ') null=', ty.is_nullable, ' table=', ty.is_table_type)
FROM sys.types ty WHERE ty.is_user_defined = 1
UNION ALL
SELECT 'Sequence', SCHEMA_NAME(sq.schema_id) + '.' + sq.name,
       CONCAT(TYPE_NAME(sq.user_type_id), ' start=', CAST(sq.start_value AS NVARCHAR(40)), ' increment=', CAST(sq.increment AS NVARCHAR(40)))
FROM sys.sequences sq
UNION ALL
SELECT 'Synonym', SCHEMA_NAME(sy.schema_id) + '.' + sy.name, sy.base_object_name
FROM sys.synonyms sy
UNION ALL
SELECT 'Role', dp.name, ''
FROM sys.database_principals dp WHERE dp.type = 'R' AND dp.is_fixed_role = 0 AND dp.principal_id > 0 AND dp.name <> 'public'
UNION ALL
SELECT 'RolePermission', CONCAT(dp.name, ' ', pe.state_desc COLLATE DATABASE_DEFAULT, ' ', pe.permission_name COLLATE DATABASE_DEFAULT, ' ', pe.class_desc COLLATE DATABASE_DEFAULT, ' ',
                                CASE pe.class WHEN 1 THEN OBJECT_SCHEMA_NAME(pe.major_id) + '.' + OBJECT_NAME(pe.major_id)
                                              WHEN 3 THEN SCHEMA_NAME(pe.major_id) ELSE '' END), ''
FROM sys.database_permissions pe
JOIN sys.database_principals dp ON dp.principal_id = pe.grantee_principal_id AND dp.type = 'R' AND dp.is_fixed_role = 0 AND dp.name <> 'public';
'@

# One row per table: its row count and a checksum over the columns whose
# values are the same on every build (see -IncludeData in the help).
$dataSql = @'
SET NOCOUNT ON;
DECLARE @sql NVARCHAR(MAX) = N'';
SELECT @sql += CONCAT(IIF(@sql = N'', N'', N' UNION ALL '),
    N'SELECT ''Data'' AS Kind, N', QUOTENAME(s.name + '.' + t.name, ''''), N' AS Name, CONCAT(''rows='', COUNT(*), '' checksum='', ',
    ISNULL(N'CHECKSUM_AGG(BINARY_CHECKSUM(' + cl.Cols + N'))', N'''-'''), N') AS Detail FROM ', QUOTENAME(s.name), N'.', QUOTENAME(t.name))
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
OUTER APPLY (
    SELECT STRING_AGG(CAST(QUOTENAME(c.name) AS NVARCHAR(MAX)), N',') WITHIN GROUP (ORDER BY c.name) AS Cols
    FROM sys.columns c
    WHERE c.object_id = t.object_id
      AND c.is_computed = 0
      AND TYPE_NAME(c.system_type_id) NOT IN ('date', 'datetime', 'datetime2', 'smalldatetime', 'datetimeoffset', 'time',
                                              'timestamp', 'uniqueidentifier', 'xml', 'varbinary', 'binary', 'image', 'text', 'ntext', 'sql_variant')
) cl
WHERE t.is_ms_shipped = 0 AND t.name <> 'SchemaVersions';
EXEC sp_executesql @sql;
'@

# Removes -- and /* */ comments (nested, as T-SQL allows), leaving string
# literals and quoted identifiers alone.
function ConvertTo-SqlWithoutComment {
    param([string]$Text)

    $out = New-Object System.Text.StringBuilder
    $i = 0
    while ($i -lt $Text.Length) {
        $c = $Text[$i]
        $next = if ($i + 1 -lt $Text.Length) { $Text[$i + 1] } else { [char]0 }
        if ($c -eq "'" -or $c -eq '"' -or $c -eq '[') {
            $close = if ($c -eq '[') { ']' } else { $c }
            $end = $i + 1
            while ($end -lt $Text.Length) {
                if ($Text[$end] -eq $close) {
                    if ($end + 1 -lt $Text.Length -and $Text[$end + 1] -eq $close) { $end += 2; continue }
                    break
                }
                $end++
            }
            [void]$out.Append($Text.Substring($i, [Math]::Min($end + 1, $Text.Length) - $i))
            $i = $end + 1
        } elseif ($c -eq '-' -and $next -eq '-') {
            while ($i -lt $Text.Length -and $Text[$i] -ne "`n") { $i++ }
        } elseif ($c -eq '/' -and $next -eq '*') {
            $depth = 0
            do {
                if ($Text[$i] -eq '/' -and $i + 1 -lt $Text.Length -and $Text[$i + 1] -eq '*') { $depth++; $i += 2 }
                elseif ($Text[$i] -eq '*' -and $i + 1 -lt $Text.Length -and $Text[$i + 1] -eq '/') { $depth--; $i += 2 }
                else { $i++ }
            } while ($depth -gt 0 -and $i -lt $Text.Length)
            [void]$out.Append(' ')
        } else {
            [void]$out.Append($c)
            $i++
        }
    }
    return $out.ToString()
}

function Get-CatalogRow {
    param([string]$DatabaseName, [bool]$WithData, [bool]$WithoutComments)

    $connectionString = Format-BlueTrackConnectionString -SqlServerInstance $SqlServerInstance -DatabaseName $DatabaseName `
        -UseWindowsAuth $UseWindowsAuth -SqlCredential $SqlCredential
    $connection = New-Object System.Data.SqlClient.SqlConnection $connectionString
    $connection.Open()
    try {
        $queries = @($catalogSql)
        if ($WithData) { $queries += $dataSql }
        $rows = @{}
        foreach ($query in $queries) {
            $command = $connection.CreateCommand()
            $command.CommandText = $query
            $command.CommandTimeout = 600
            $reader = $command.ExecuteReader()
            while ($reader.Read()) {
                $detail = [string]$reader['Detail']
                if ($WithoutComments -and $reader['Kind'] -eq 'Module') { $detail = ConvertTo-SqlWithoutComment $detail }
                # Whitespace (line endings, indentation) never matters.
                $detail = ($detail -replace '\s+', ' ').Trim()
                $rows["$($reader['Kind'])|$($reader['Name'])"] = $detail
            }
            $reader.Close()
        }
        return $rows
    } finally {
        $connection.Close()
    }
}

$reference = Get-CatalogRow -DatabaseName $ReferenceDatabase -WithData $IncludeData.IsPresent -WithoutComments $IgnoreComments.IsPresent
$difference = Get-CatalogRow -DatabaseName $DifferenceDatabase -WithData $IncludeData.IsPresent -WithoutComments $IgnoreComments.IsPresent

$differences = New-Object System.Collections.Generic.List[string]
foreach ($key in (@($reference.Keys) + @($difference.Keys) | Sort-Object -Unique)) {
    $kind, $name = $key -split '\|', 2
    if (-not $difference.ContainsKey($key)) {
        $differences.Add("Only in ${ReferenceDatabase}: $kind $name")
    } elseif (-not $reference.ContainsKey($key)) {
        $differences.Add("Only in ${DifferenceDatabase}: $kind $name")
    } elseif ($reference[$key] -cne $difference[$key]) {
        $show = { param($text) if ($text.Length -gt 300) { $text.Substring(0, 300) + '...' } else { $text } }
        $differences.Add("Different: $kind $name`n    ${ReferenceDatabase}: $(& $show $reference[$key])`n    ${DifferenceDatabase}: $(& $show $difference[$key])")
    }
}

if ($differences.Count -eq 0) {
    Write-Host "No differences between $ReferenceDatabase and $DifferenceDatabase ($($reference.Count) items compared)."
    exit 0
}
$differences | ForEach-Object { Write-Output $_ }
Write-Host "`n$($differences.Count) difference(s)."
exit 1
