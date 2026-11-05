# BlueTrack Settings Reference: Data, Risk Scoring, the Deployment Page and the Installer

**For BlueTrack administrators**

This is the second part of the settings reference. The first part, [Admin_SettingsReference.md](Admin_SettingsReference.md), explains how to read the tables and covers Global Application Configuration and sign-in and access. This part covers data sources, risk scoring and the other data pages, the Deployment page and its health checks, and the installer's settings. The third part, [Admin_SettingsReference_Notifications.md](Admin_SettingsReference_Notifications.md), covers the Secrets Store, credentials, LDAP, notifications, SIEM forwarding and API keys. All three are published together as the Administrator Settings Guide.

## 3. Data and risk scoring

### Data Sources: AD account discovery and DNA

**Admin > Data Sources** (permission `ManageDataSources`), the **Active Directory account discovery** and **CyberArk Discovery & Audit (DNA)** sections.

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Runs at | When AD account discovery runs each day, server time (HH:mm). **Run now** runs it at once. | 04:30 | After the 04:00 data feeds | It reads every inventoried Access Group's members in each domain, which takes a while on a large directory. |
| Domains (ticks) | Which LDAP domains AD account discovery reads: **Use as a data source** on each domain. Only enabled domains are read. **Add a domain for discovery only** opens a new domain on Credentials & LDAP that BlueTrack's own lookups don't use. | Every existing domain ticked | Tick the domains whose accounts you want discovered | A domain can serve BlueTrack's lookups, discovery, or both. |
| DNA workbook | Uploads DNA's account-list workbook (.xlsx), the older format or the newer DNA report; it replaces the previous upload. A data feed of type **DNA (Discovery & Audit) workbook (.xlsx)** can import it from a folder each night instead. | None | A feed on the folder DNA saves its report to | The accounts appear on Reports > DNA Accounts and count toward **Everything discovered**; the hard-coded credentials go to Reports > DNA Hard-Coded Credentials, with passwords masked. |
| DNA tracking rules | After each DNA import, each enabled rule tracks the DNA accounts matching all its conditions as BlueTrack accounts at the first stage. **Apply the rules now** runs them at once. | None | Use **Would track now** to check a rule before turning it on | Brings the DNA findings you act on into Account Progress without ticking them by hand. |
| Keep DNA data (days) | Imports older than this are deleted each night. **Purge DNA data** deletes them all now. Tracked accounts stay either way. | Blank (kept until the next import or a purge) | Your retention policy for scan data | DNA data names hosts and accounts. |
| Columns that need the ViewDnaSensitiveColumns permission | The DNA columns anyone without `ViewDnaSensitiveColumns` sees blank on Reports > DNA Accounts, and can't search or sort by. The finding, the account name and its BlueTrack link can't be restricted. | None ticked | Tick the columns your policy treats as sensitive | Lets more people use the report without seeing everything DNA found. |

### Data Sources (feeds)

**Admin > Data Sources** (permission `ManageDataSources`). Each feed imports the newest matching CSV file from a folder, using the same imports as the Bulk Actions uploads. The schedule and business hours are on Global Application Configuration.

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Name | The feed's name, up to 200 characters. | Blank | Say what the file is and where it comes from | Makes run history easy to read. |
| Import | What the file holds: Target inventory, Access group inventory, Access group → target map, Account → access group membership, Account → target map, Applications, Safe → application assignments, Account → application assignments, Account inventory (accounts from another system), or DNA (Discovery & Audit) workbook (.xlsx), which reads the newest matching workbook as the DNA upload does. | None | The import the file matches | The file must have the columns of that import's Bulk Actions template, or match a mapping profile. Account inventory has no template: see below. |
| Source system | Account inventory only: the system the accounts come from. **+ New source system** adds one (a name, and a short code of letters, digits and underscores). | None | One source system per system you import from | Each source system can have only one account inventory feed, because its accounts missing from the file are marked deleted. |
| Skip group rows | Account inventory only: skip rows whose `UserName` starts and ends with `*` and contains the word `group`, in any case. A pending-accounts export lists its local and domain groups that way, for example `*Domain Admins Group*`. | Off | On for a pending-accounts export | Skipped rows aren't accounts and aren't errors; the run's summary says how many were skipped. A group an earlier run imported is marked deleted by the next run. |
| Mapping profile | For the five risk-scoring imports: which Import Mapping Profile translates the file's column names. | None | Choose the profile when the file isn't in template format | None means the file uses the template's own column names. |
| Folder path | A full local or UNC path, up to 500 characters. | Blank | A dedicated feed folder inside the server's allowed folders | The App Pool's account reads it, so it needs read access. Network shares need to be in the allowed folders. |
| File name pattern | The file to pick, up to 260 characters. A date format in braces, such as `{yyyy-MM-dd}`, becomes today's date; `*` and `?` are wildcards. | Blank | Include the date, for example `targets_{yyyy-MM-dd}.csv` | A dated name stops yesterday's file being imported again by mistake. |
| Run order | The order enabled feeds run in. | 0 | Inventories first, then the maps that refer to them | A map that refers to targets or groups loads cleanly once they exist. |
| Enabled (runs nightly) | Whether the feed runs at the nightly run time. | On | On once **Test** passes | Test shows the file a run would pick and any missing required columns. |

The matched file is left in place after import. Only one run happens at a time.

**Account inventory feeds** add another system's accounts to Account Progress, at **Discovered**, under the feed's source system. The file needs `UserName` and `Address`; `AccountName` (default `username@address`), `PlatformName` (a CyberArk platform's name or ID, used to suggest the account type), `OwnerName` and `Notes` are optional. An account is identified by its user name and address (ignoring case) within the source system. The two together can be up to 400 characters; a longer row is reported as a row error. On later runs, the account's name, address and platform are updated from the file; its owner, notes and stage are BlueTrack's and aren't overwritten. An account missing from the file is marked deleted, and comes back if it reappears. A file with no valid rows fails the run and changes nothing. These accounts never move past Discovered on their own; after each run, an account CyberArk also has (the same user name and address, ignoring case and spaces) is **linked** to the CyberArk account and moves to Onboarded to Vault, recorded in the Audit Log. A linked account is hidden from Account Progress and the reports (the **Linked feed accounts** filter shows it). Feeds run after the nightly CyberArk load, so an account onboarded today is linked that night. An account that only nearly matches (the same user name, with addresses that agree by host name or DNS) is suggested on **Reports > Possible Account Matches** instead, for someone with `ConfirmReconciliation` to link or dismiss. The DNS lookups are made by the App Pool's account during the run.

### CyberArk sources

**Admin > Data Sources**, the "CyberArk sources" section at the top, then **Edit CyberArk sources** (permission `ManageDataSources`). These settings are read by the nightly Import+Load job's Import step (02:00); changes apply from the next nightly run. Privilege Cloud has no Run now; Self-Hosted has **Pull now** (below). The installer sets them on first install (section 5, "Nightly import"); until they're saved, the page shows "Not set up yet" and the nightly import fails.

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Import Privilege Cloud | Import the Privilege Cloud CSV exports. At least one source must be on. | Installer's answer | On if you have Privilege Cloud | A source that is on but missing fails the job each night. |
| Export folder | The folder holding the exports, up to 400 characters. Must be inside the server's allowed folders (without any set: local folders only, no network shares). | Installer's answer | A local folder on the SQL Server | SQL Server's service account reads the files, so it needs read access. |
| Platforms, Users, Groups, Local group members, Safes, Accounts, Entitlements file | The file name of each export, up to 200 characters. A date format in braces, such as `{yyyy-MM-dd}`, becomes the run's date; `*` and `?` aren't allowed. | `Export_PlatformsList.csv`, `Export_UsersList.csv`, `Export_GroupsList.csv`, `Export Local Group Members {yyyy-MM-dd}.csv`, `Export_SafesList.csv`, `Export_AccountsList.csv`, `Export Entitlements {yyyy-MM-dd}.csv` | The defaults, unless your exports are named differently | SQL Server needs the exact file name. |
| Import Self-Hosted | Read Self-Hosted from PVWA each night (Self-Hosted Direct); its settings are below. | Installer's answer | On if you have Self-Hosted | As above. |

**Self-Hosted Direct.** BlueTrack reads Safes, accounts, Safe members, users, groups and platforms from PVWA's REST API each night, at the pull time, before the import job. It sees only what its account can see, and doesn't read pending (discovered) accounts. It needs PAM Self-Hosted 14.2 or later.

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Include CyberArk's built-in Safes | Also read the Vault's internal Safes (PasswordManager_Accounts, PVWAConfig and the like) and their members, which PVWA's REST Safe list leaves out; they show as CyberArk internal Safes. | Off | On to review whether that data is useful | The first pull after turning it on counts more Safes, which **Allowed change** may need you to accept. |
| Other PVWA addresses | More PVWAs, one per line (up to 5), each `https://`. A pull or **Test** signs in to the PVWA address first, then each of these in order, moving on only when one can't be reached (no connection, no answer, or 502/503/504); a refused credential is never tried on another. The pull's message and Test name any PVWA it passed over. | Blank | Your other PVWAs, when they aren't behind one load balancer | Keeps the nightly pull going when one PVWA is down. |
| PVWA address | The PVWA's address, `https://` only; the `/PasswordVault` part is optional. Up to 400 characters. | Blank | Your PVWA's address, as reachable from the BlueTrack server; with several PVWAs, the load balancer's address (see below) | The credential's password is sent over this connection, so plain HTTP is refused. |
| Credential | A named credential from **Admin > Credentials & LDAP**. A CyberArk CP or CCP credential is fetched from the Vault at each pull. **Create a credential** opens that page and returns here with the new one chosen, keeping what you typed. | None | A CP or CCP credential for a dedicated PVWA user | The password then stays in the Vault and can be rotated there. |
| Logon method | **CyberArk** or **LDAP**; both use the credential's user name and password. | CyberArk | The method the PVWA user is set up for | Windows and SAML logon aren't supported. |
| Pull time limit (minutes) | A pull (nightly or **Pull now**) still running after this long is stopped and logged as failed; the staged data the nightly load reads is left as it was. Allowed: 5 to 720. | 45 | 45, or longer if your pulls take longer | A PVWA that stops answering can't hold the pull past the import. Check how long your pulls take in the pull log's **Details**. |
| Also re-read account details that haven't changed | Each pull reads an account's folder, device type and linked accounts again only when CyberArk says its properties changed; moving an account to another folder may not count. When on, each pull also re-reads accounts whose details were read more than **Re-read details older than (days)** ago (1 to 365), at most **Most accounts re-read per pull** (1 to 100,000), the oldest first. | Off; 7 days; 1,000 | On if archive rules use **Folder** | One PVWA call per account, counted against the pull time limit. |
| Pull time | When the nightly pull runs, server time (HH:mm). | 01:30 | Early enough to finish well before the 02:00 import | The import fails if the latest pull didn't succeed or is more than 20 hours old. |
| Allowed change (%) | The most the number of Safes, or of accounts, may change from the last good pull before a pull is rejected. A change of 5 or fewer is always allowed. Allowed: 0 to 100. | 20 | 20 | A lost Safe membership or a wrong credential looks like Safes and accounts disappearing; without this check the next load would mark their accounts deleted. |

**Several PVWAs behind a load balancer.** Give the load balancer's address. CyberArk requires a load balancer in front of PVWA to keep sticky sessions (PAM Self-Hosted 15.2, "Multiple PVWA installations"), and BlueTrack relies on that: each pull and each **Test** signs in once and makes every call after it from the same server, through one connection that keeps the load balancer's cookies, then signs out. With stickiness by cookie or by client address, the whole pull reaches one PVWA. Without sticky sessions, calls after the sign-in may reach a PVWA that doesn't accept the session, and the pull fails; BlueTrack has no list of PVWA addresses to try in turn.

If the chosen credential is a CyberArk CP or CCP credential and that backend isn't set up on **Admin > Secrets Store Configuration** (CP needs an App ID; CCP a Base URL and an App ID), the page says so, with **Open Secrets Store**. The PVWA user's Safe memberships and Vault authorizations are in the Operations Guide, "Self-Hosted Direct Pulls".

**The pull log.** Under "Self-Hosted Direct pulls", Data Sources lists recent pulls: when each started, who started it, the result (Succeeded, Rejected or Failed) and what it read (Safes, accounts, users, groups; a rejected pull shows the Safe and account counts before and after). **Details** shows the whole pull: who started it, how long it took, every message, all the counts, what the result means and what to do.

- **Pull now** starts a pull straight away (inside business hours, after a confirmation). It replaces the staged Self-Hosted data and, when it succeeds, starts the nightly **Import and Load** SQL Agent job, so the accounts change within minutes; the pull's row shows the load's result. If the job can't be started (the app's account lacks the SQL Agent role from the installer's `Db.AppPoolAccess` step, the SQL Server Agent is stopped, or the job is already running), the row says why and the accounts change at the next nightly job.
- **Accept the change and pull**, offered after a **Rejected** pull, pulls again and accepts whatever PVWA returns, after a confirmation. Accounts PVWA no longer returns are marked deleted at the next load.
- **Cancel pull**, while a pull runs (nightly or Pull now), stops it after a confirmation. It is logged as failed, and the staged data is left as it was.
- After a failed or rejected latest pull, Data Sources shows a banner saying tonight's import will fail until a pull succeeds, and the Admin menu shows ⚠ next to **Data Sources**. A rejected pull's message names the Safes it no longer returns and the new ones (up to 20 of each).

Pull now, Accept and Cancel are recorded in the Audit Log. The **SelfHostedPullFailed** notification (section 7) emails a failed or rejected pull.

**Test** checks the values on the page, saved or not: with Self-Hosted on, it logs on to PVWA with the credential, reads one Safe and one account to show it can list them, and reports the totals PVWA gives (and whether a pull of that many would pass **Allowed change**), which takes seconds; **Test with a full read** reads everything a pull reads (without the classic account details) and counts it, which takes as long as a pull; with Privilege Cloud on, it checks that the export folder and today's seven files exist as the App Pool's account sees them. It also reads the header row of each Privilege Cloud file and reports any column the import needs that the file lacks ("the import would fail"), and lists optional columns that will import as empty. During the import it's SQL Server's service account that reads the files, so it needs read access too. Every save is recorded in the audit log, with each changed field.

**Columns are read by name.** The Privilege Cloud import finds each column by its header, so the order of the columns doesn't matter, columns BlueTrack doesn't use are ignored, and both the older and the current Users and Groups export layouts load. A missing optional column loads as empty. A missing required column fails the nightly import with a message naming the file and the columns; the **NightlyImportFailed** email (section 7) carries that message. The Self-Hosted import is unchanged.

### Import Mapping Profiles

**Admin > Import Mapping Profiles** (permission `ManageTargets`). A profile maps a source file's column names to BlueTrack's internal field names, for the five risk-scoring imports (TargetInventory, AccessGroupInventory, AccessGroupTargetMap, AccountAccessGroupMembership, AccountTargetMap).

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Feed Type | Which import the profile is for. | TargetInventory | The import the file feeds | A profile only applies to its own import. |
| Profile Name | The profile's name; unique per feed type. | Blank | The source tool and version, for example "CMDB export v2" | A new file format gets a new profile. |
| Active | Whether the profile is offered when a data feed or a Bulk Actions upload chooses a profile. | On | On for the profiles in use | A profile is used only when it's chosen; none chosen means the template's own column names. |
| Description | Free text. | Blank | Where the file comes from | Helps the next administrator. |
| Source Column | The column header exactly as it appears in the file. | Blank | Copy it from the file | Must match the header. |
| Internal Field | The BlueTrack field the column feeds. | Blank | A field name from that import's template | Only the template's field names are used. |
| Required | A row with this value missing is reported as an error. | Off | On for identifying fields | Catches incomplete rows instead of loading them. |
| Default | The value used when the column is blank or missing and not required. | Blank | Only where a sensible default exists | A default fills gaps silently, so use it sparingly. |

A file in the template's own format needs no profile.

**Fill a profile from a sample file.** Under **Detect from a sample file** on the profile's edit page, pick a **Sample CSV file**. Only its header row is read, in your browser; the file isn't uploaded. Each field is matched to a column with the same name (ignoring case, spaces, underscores and punctuation), then to a common alternative name, and the page lists what it filled in, what it couldn't match and the columns it didn't use. A field you've already mapped to a different column is changed only if you choose **Replace with the file's columns** (**Keep my mappings** leaves it). Check the result before you save.

**Bulk Actions column check.** On the Targets and Access Groups **Bulk Actions** pages, with no mapping profile chosen, picking a file lists the columns read by name and any required ones missing. **Create a mapping profile from these columns** opens a new profile filled in from the file's headers.

### Target Match Review

**Admin > Target Match Review** (permission `ManageTargets`) has no settings. When an imported target matches an existing one only weakly (for example by IP address alone, which DHCP or NAT can reuse), the row waits here instead of being merged. Each row shows the identifier, the candidate target and the imported target (its name, source and risk score). Resolve it with:

- **Link**: keeps the imported row as its own target, linked to the candidate, or to another target found by typing at least 2 characters of its name or any identifier in **Link To**. The IP address stays on the existing target. Anything that reaches either target counts once, at the risk score of the target it's linked to.
- **New Target**: keeps it as a separate, unlinked target.
- **Ignore**: creates nothing, after a confirmation.

Every choice is remembered. A later import of the same row (the same name and discovery source) goes straight to the target that Link or New Target made, and an ignored row is skipped (the Bulk Actions result says how many). Linked targets are hidden on **Targets** unless **Linked targets** is set to **Show**. The **Edit Target** page shows the target a linked target belongs to, or a target's **Linked Targets**, with **Unlink**: an unlinked target counts at its own risk score again and isn't linked again automatically. Every resolution, link and unlink is recorded in the audit log.

### Field Metadata

**Admin > Field Metadata Management** (permission `ManageFieldMetadata`). The list of fields shown on the Account Progress edit form. A new install has eleven fields: Blueprint Stage and Status (both required), Risk Level, Account Type, Source of Record, Application, Owner Name, Business Unit, Target Remediation Date, Actual Completion Date and Notes.

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Field Name | The underlying field. | As seeded | Don't change the seeded names | The edit form matches each field to its stored value by this name. |
| Display Label | The label users see. | As seeded | Your organization's terms | Change labels freely; it changes nothing stored. |
| Field Type | How the input is shown: `Text`, `Date`, `Dropdown` or `TextArea`. | As seeded | Keep the seeded type | The type must suit the stored value. |
| Reference Table | For Dropdown fields, the list the choices come from. | As seeded | Keep the seeded table | The choices come from that list. |
| Required | The form can't be saved without a value. | Stage and Status | Stage and Status, plus any your process requires | Required fields keep the reports complete. |
| Display Order | The order of fields on the form. | 10, 20 ... 100 | Your preferred order | Gaps of 10 make reordering easy. |
| Required permission | The permission needed to change this field by hand, on top of `EditAccountProgress`. Enforced on the edit page, in bulk edit and in the Risk Exception import; someone without it sees the field read-only, with the permission named, and bulk edit doesn't offer it. Progress Rules and the nightly load aren't affected. | Source of Record: `CuratePlatformMapping` (Admin); Actual Completion Date: `ConfirmReconciliation` (Admin, Approver); Risk Level: `ManageTargets` (Admin, Analyst); the rest none | The defaults | Source of record normally comes from Platform Mapping, and a completion date confirms work an approver signs off. Set with **Required Permission** on the Edit Field page, and shown in the field list; the upgrade sets these defaults only where a field had none, so your own choice is kept. |

### Risk Score

**Admin > Risk Score** (permission `ManageRiskScoreBands`) has three tabs. **Bands** turn a risk score (0 to 1000) into a label.

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Name | The band's label. | Low, Medium, High, Critical | Your organization's risk terms | Matches language people already use. |
| Min Score / Max Score | The band's score range. Min must not be greater than Max; bands may not overlap. | 0-250, 251-500, 501-750, 751-1000 | Cover 0 to 1000 with no gaps | A score in a gap gets no label. |
| Risk Order | The sort order, lowest risk first. | 10, 20, 30, 40 | Keep gaps of 10 | Reports sort by it. |

**Defaults** (changes are in the Audit Log as RiskDefaultsChanged):

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Target made from an account's address | The score of each target the nightly load makes for an account address that has none. Targets made before keep theirs. | 0 | Your typical server's score, if most are alike | Otherwise a new server counts for nothing until someone scores it. |
| New target / New access group | The score the Add Target and Add Access Group forms start with. | 0 | As above | Saves typing the usual value. |
| Hard-coded secret | Risk value of a hard-coded credential DNA found: it counts for the target it was found on (so every account that reaches that target) and for the account it names. | 300, counts | Keep | A secret in a file can be read by anyone with access to the system. |
| Hard-coded privileged secret | The same, when DNA lists the account as privileged (its Account Category or a privileged domain group) or its AD account is classed Admin. | 600, counts | Higher than the plain one | A privileged secret in a file is worse. |
| Pass-the-Hash vulnerable / Non-compliant | Risk value of a DNA account so marked, on the BlueTrack account it is linked to. | 400 / 200, counts | Keep | |

A finding counts like a target with that score in the risk algorithm (Admin > Configuration), so a high value raises a low score a lot and a high score little. Set a value to 0, or untick **Counts**, to leave it out. A changed value marks every score to recalculate: use **Recalculate Now** on Reports > Risk Score, or wait for the nightly load.

**Findings** lists the findings now: each kind on its target or account, with how many DNA rows gave it. They are rebuilt after each DNA import and before each recalculation; **Refresh from DNA** rebuilds them at once. The file and line of a hard-coded credential are only on Reports > DNA Hard-Coded Credentials (`ViewDnaSensitiveColumns`).

### Account Classification

**Admin > Account Classification** (permission `ManageDiscoveredAccounts`). Separates a person's day-to-day account from their secondary and admin accounts on **Reports > Discovered Accounts**: each discovered account gets an **Account Class** (Primary, Secondary, Admin or Unknown) and an **Owner** (the person's primary account in the same domain). Nothing changes until you turn it on and add rules.

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Classify discovered accounts | Works out the class and owner. Off: the columns stay empty (a manual override still shows) and discovery reads no extra attributes. | Off | On, once rules are added | The next discovery run reads the attributes the rules need. |
| Class when no rule matches | Unknown or Primary. | Unknown | Unknown | Primary would also make every unmatched service account a person's main account. |
| Find the owner by a shared employee ID | For an account that isn't Primary, the owner is the one enabled account in the same domain with the same employee ID whose class is Primary; for a Secondary or Admin account with none, the one that matches no rule. If more than one could be, the owner is left empty (ambiguous). | Off | On if your directory fills the ID on both accounts | Finds owners whose admin account isn't named after them. |
| Employee ID attribute | employeeID or employeeNumber. | employeeID | Whichever your HR feed fills | |

**Rules.** Each rule has a condition and a class (Primary, Secondary or Admin), can be enabled or disabled, and is edited on its own page (click its condition). Attribute rules are checked first, then Name rules, then OU rules; within a type, in the order shown (**Up** / **Down**). The first enabled rule that matches sets the class.

| Rule type | Condition | Example |
|---|---|---|
| Attribute | One of adminCount, employeeType, extensionAttribute1 to 15, description, title, department; **equals**, **contains**, **starts with** (case ignored) or **is set**. No other attribute can be used. | adminCount equals 1 → Admin; extensionAttribute5 equals Admin → Admin |
| Name | sAMAccountName or userPrincipalName **starts with** (prefix), **ends with** (suffix) or **matches pattern** (`*` any text, `?` one character). | sAMAccountName starts with `adm.` → Admin; pattern `*-a` → Secondary |
| OU | The distinguished name **contains** or **ends with** an OU path (case and spaces after commas ignored). | contains `OU=Admin Accounts` → Admin |

A Secondary or Admin **Name** rule on sAMAccountName also gives the owner: the name without the prefix or suffix (`adm.jsmith` is owned by `jsmith`), or the text a pattern's single `*` matched. The owner is used only if that account was found: it's a discovered account, or the discovery run read it from AD. The name rule is tried before the employee ID. A Primary account is its own owner.

Saving the settings, or adding, changing, moving or deleting a rule, reclassifies every discovered account at once from what the last discovery run read; it doesn't contact AD. **Reclassify Now** does the same on request; **See the result on Discovered Accounts** opens the report. An owner outside the discovered accounts for a new name rule appears after the next discovery run. Every change is recorded in the Audit Log (event **AccountClassification**).

### Platform Mapping

**Admin > Platform Mapping** (permission `CuratePlatformMapping`, held by Admin). Gives each CyberArk platform the **account type** and **source of record** its accounts start with: Privilege Cloud's own platform list, and for Self-Hosted one platform per policy (the account's PolicyID), added by the nightly load. A mapping is a default, not a rule: both stay editable on each account.

The page lists every platform, the ones with no mapping first, with its source, how many accounts it has and how many of those have no account type. **Search** and **Only platforms with no mapping** narrow the list. Choose an **Account type** (required) and optionally a **Source of record** and **Notes** on a platform's row, then **Save**. **Remove** takes a mapping away (accounts keep what they already have).

**Suggestions.** An unmapped platform can show a suggestion under its account type, with the reason: the mapping of the same platform in the other CyberArk source, or one that fits a word in the platform's ID or name (for example WinDomain → Domain Account / Active Directory, UnixSSH → Local/OS Account / Local OS, Oracle → Database Account / Local Database). **Use** fills that row's drop-downs; **Fill in N suggestions** fills every row shown that has a suggestion and no account type chosen. Nothing is saved until you click **Save** on each row, so check each one first. Group platforms get no suggestion.

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Account type | The account type a new account on the platform starts with, from the fixed list (Domain Account, Local/OS Account, Cloud IAM, ...). | Not mapped | Set one for every platform that has accounts | Reports and filters group accounts by it; an unmapped platform's accounts start with none. |
| Source of record | Where the account really lives (Active Directory, LDAP, Local OS, ...). Optional. | None | Set it when the platform implies it, for example WinDomain implies Active Directory | Lets you see which accounts an identity system should be managing. |
| Notes | Free text about the choice (up to 500 characters). | Blank | Why you chose it, for the next person | The platform's name alone rarely says. |
| When saving, also fill accounts already tracked | Gives the account type (and source of record) to accounts on the platform that have none. An account that already has one is never changed. | On | On, the first time you map a platform | Otherwise only accounts first tracked after the mapping get the default. Each save is in the Audit Log with how many accounts it filled. |

### Progress Rules and Blueprint Stages

Needs `ManageProgressRules` (Admin role). Changes are in the Audit Log (ProgressRuleChanged, BlueprintStageChanged).

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Blueprint Stages: name and order | The stages accounts go through; **Move up** / **Move down** reorder them. | Discovered, Assessed / Prioritized, Onboarded to Vault, Managed / Rotation Enabled, Verified / Compliant | Your program's own stages | Reports, Trends and the account form use them. |
| Start | Where new accounts start. Exactly one stage. | Discovered | The first stage | Every import adds new accounts there. |
| KPI milestone | Which stage the Onboarded, Managed and Compliant KPIs count from (that stage or later); they must stay in that order. | Onboarded to Vault, Managed / Rotation Enabled, Verified / Compliant | Keep them on the matching stages | The KPI Summary, Dashboard and Weekly Summary follow them. |
| Delete | Only a stage no account, rule or history uses, and that isn't the start stage or a milestone. | | Rename instead once accounts have used a stage | History keeps its stage. |
| Progress Rule: The rule | **Moves accounts to a later stage**, or **Archives accounts (any stage)**: sets matching accounts to the status Archived, which is out of scope for the KPIs and counted on its own. Archive rules run first; stage rules don't move Archived or Risk Accepted / Excluded accounts. An archived account stays Archived until someone changes its status, and isn't archived again until the archive rules have stopped matching it. | Moves accounts | An archive rule for accounts your tools retire, for example **Why the CPM doesn't manage it** is ADArchive and **Folder** is Archive | Several hundred retired workstation accounts otherwise sit in the numbers. |
| Progress Rule: From, To | Accounts at From move to To (a later stage; the next one by default). Not used by an archive rule. | | | |
| Progress Rule: conditions | All must match. The field list is in alphabetical order (a new condition starts on Source). A **Source** condition's value is picked from the source systems set up here: one from a list for is / is not, any number by ticking for is one of / is none of, and suggestions for contains / starts with; a saved value that is no longer a source system stays listed. Text fields include **Folder** (Self-Hosted Direct; Root is the top of the Safe) and **Why the CPM doesn't manage it** (the CPM disable reason). Yes/No fields (Managed by the CPM; Platform requires PSM, Self-Hosted only; **Safe access compliant**, empty when no Safe access policy applies; **Compliance reviewed**): is Yes or No, is empty. Text fields: is, is not, contains, does not contain, starts with, is one of, is none of, is empty, is not empty (case doesn't matter, on any SQL Server; "not" also matches empty). Date fields: within the last N days, more than N days ago. | The seeded rule: Source is one of PRIVCLOUD, SELFHOSTED; Safe name doesn't contain `_Pending` | Use **Test** before saving | Test shows how many accounts at the From stage the rule would move tonight. |
| Enabled | Whether the nightly load uses the rule. | On | | Turn a rule off to stop it without losing it. |

Drop-down lists across BlueTrack, the rule fields among them, are in alphabetical order, except where the order means something (stages, risk levels).

### Safe Access Policies

Needs `ManageSafeAccessPolicies` (Admin role). Changes are in the Audit Log (SafeAccessPolicyChanged). Each night, after the Safe members are loaded and before the Progress Rules, every Safe that has accounts is checked against the **first enabled policy, in order, whose Safe names match it**; **Check now** does the same at once. The results are on **Reports > Safe Access**, and on each account's page under **Compliance**.

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| Safe role: permissions | The Safe permissions a member must hold to count as holding the role. **Exactly these permissions**: and no others. | Seeded "End user": ListAccounts, UseAccounts, RetrieveAccounts (an example) | One role per kind of access in your Safe design | Rules name roles, not permissions. A role a rule uses can't be deleted. |
| Policy: Safes it applies to | A Safe name pattern: `*` is any text; case doesn't matter. | | One policy per family of Safes, for example `APP_*` | |
| Policy: Order | Lower first. A Safe is checked against the first enabled policy whose pattern matches it. | After the last policy | Narrow policies before broad ones (`*` last) | A Safe is checked against one policy only. |
| Required rule | A member whose name matches the pattern holds the role. `{SafeName}` in the pattern is the Safe's own name, `*` any text. **With people in it**: a matching group must have members (Vault group members, or AD users read for that group). | The example: `{SafeName}_Users` holding End user, with people | Your access group naming | "No member matches", "doesn't hold" and "has no people in it" say what is missing. |
| Forbidden rule | No member matching the pattern; with a role chosen, no matching member holding that role. | The example: `Vault Admins`, any membership | | Each forbidden member found is listed. |
| Enabled | Whether the nightly check uses the policy. | The example is **off** | Enable after editing the example's names | |

Reviewing needs `ReviewAccountCompliance` (Admin and Approver roles): **Mark reviewed** on an account's page, or **Review accounts** on Reports > Safe Access for every account of a Safe not reviewed yet. A review lasts until the account moves to another Safe or someone clears it, and is in the Audit Log (AccountComplianceReviewed). Use the Progress Rule fields **Safe access compliant** and **Compliance reviewed** to move accounts to Managed and Compliant.

### Application ↔ Safe Mapping

**Admin > Application ↔ Safe Mapping** (permission `CurateApplicationMapping`) has no settings. It holds data: each application's code, name, description, owner and technical contact, and which application each safe belongs to. Keep it current, because the risk reports group accounts by application. An account can also be assigned to an application on its own edit form (**Application**), in addition to its safe's; that is how an account with no safe, such as a pending account, belongs to one. When an account's own application and its safe's differ, both apply: an Active risk exception on either one covers the account, and Reports > Trends' Application filter counts it under either. Many accounts can be given one with **Bulk edit…** on Accounts, or with the **Account → Application Assignments** CSV on this page's **Bulk Actions** (or a data feed of that type). Each Bulk Actions import has **Check file**, which reports what the file would do and its row errors without changing anything.

The Applications table shows what uses each application (**In use**: Safes, accounts, targets and exceptions). An application can be deleted only when nothing uses it. The Safes table has **Find a Safe:** and **Only Safes with no application**, to find the Safes still to assign.

## 4. Deployment page and health checks

**Admin > Deployment** (permission `ViewDeploymentInfo`; **Backup App** also needs `TriggerBackup`) shows the environment name (the installer's `-Environment` answer), the version and build time, the health checks, SQL Server's last backup times and recent BlueTrack backups.

**Version.** The Deployment page and **About** (in the menu with your name) show the same version. A release shows its number, for example `1.1.0`. A build made between releases shows the release it follows, how many changes came after it and which one, for example `1.1.0-dev.12+abc1234`, ending `.dirty` if it was built with uncommitted changes. A copy downloaded by `Update-BlueTrack.ps1` shows the plain version number.

| Health check | Healthy means | A problem means | What to do |
|---|---|---|---|
| SQL Server | BlueTrack connected to its database. | Unhealthy: the connection failed. | Check SQL Server is running and the App Pool's account has its login. |
| Secrets Store | The active backend has an implementation in this build. | Unhealthy: no implementation, for example CyberArkCP without the SDK. | Make another backend active. This check doesn't fetch a secret; use **Test Connection** for that. |
| Identity Providers | At least one provider is enabled, and enabled OIDC and SAML rows have their required fields. | Unhealthy: nothing is enabled. Degraded: OIDC lacks Authority or Client ID; SAML lacks SP Entity ID, IdP Entity ID or sign-on destination; or the SAML SP certificate isn't installed or the App Pool's account can't use its private key. | Fill in the fields or disable the provider; for the SP key, see `Admin_Installation.md`, "SAML signing certificate". The check doesn't test a real sign-in or an OIDC secret: always test one by hand. |
| Backup Folder | No folder is set, the folder can't be seen from this server, or only specific accounts can read it. | Degraded: a broad group can read the folder, or the App Pool's account can't read its permissions. | The next backup removes broad access, or is refused if it can't. Fix the permissions so backups aren't refused. |
| Pull and Load | Nothing has stopped moving for the **Alert after (minutes)** time (Admin > Data Sources, Import and Load), and the latest pull, Import and Load didn't fail. | Unhealthy: a pull has read nothing, or a Load procedure, Import or Snapshot has been running, for longer than that, or a run's SQL Server session ended without finishing it. Degraded: the latest pull, Import or Load failed. | Open Admin > Data Sources: Import and Load shows what the run is waiting on and has **Cancel load**. |
| SIEM Forwarding | Forwarding is off, or on and keeping up. | Degraded: the last send to the collector failed, or the oldest event waiting to be sent is more than 15 minutes old. | Check the error on **Admin > SIEM Forwarding** and use **Send Test Event**; check the collector is running and the firewall allows the server out to it. Waiting events are sent once it's reachable. |

**Backup App** writes an immediate full backup to the Backup Folder; it is never deleted automatically. Recommended: run it before any configuration change you might want to undo, and copy important backups off the server.

## 5. Installer and server settings

These are chosen when running `Deploy\Install-BlueTrack.ps1`. Each can be a parameter, a value in an answer file (`-ConfigFile`, see `Deploy\answers.sample.json`), or answered at a prompt. Never put a password in an answer file; the SQL credential is always prompted.

### Environment and database

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| `-Environment` | Development, Test, Staging or Production. A label: it names the default install folders and is shown on the Deployment page, and a Production install is never offered test data. It doesn't change how BlueTrack runs: on IIS it always runs as Production, with development-only sign-in off. | Prompted | Production for live use; Test or Staging for others | The label tells people which environment they're looking at. |
| `-SqlServerInstance` | The SQL Server instance. | Prompted | The local instance on the BlueTrack server | BlueTrack is designed for a single server. |
| `-DatabaseName` | The database name. | `BlueTrack` | `BlueTrack` | Scripts and jobs name the database; keep the default unless you host several environments on one instance. |
| `-UseWindowsAuth` | Connect to SQL Server with Windows authentication. | `$true` | `$true` | No password is stored. With SQL authentication the password sits in the settings file. |
| `-SqlCredential` | A SQL login, only when Windows authentication is off. | Prompted | Not used | See above. |
| `-TrustServerCertificate` | Skip checking SQL Server's TLS certificate. | Unset: trusted only when SQL Server is on this machine | Leave unset | A remote SQL Server's certificate is then checked, protecting the connection. |
| `-GrantAppPoolSqlAccess` | Give the App Pool's account a SQL login and only the rights BlueTrack needs (not `db_owner`). | Prompted | `$true` | The site can't reach the database without it. The account can add audit entries but not change or delete them. |
| `-InstallSqlServerAudit` | *Record audit log changes in the Windows Security log*: a SQL Server audit of every change to or deletion of audit entries and their seals, by anyone, including database administrators (step `Db.SqlServerAudit`). When SQL Server is on this server it also turns on the Windows audit policy *Application Generated* and gives the SQL Server service account the *Generate security audits* right and access to the Security log. | Prompted (Yes) | `$true` | The Security log is harder to change unnoticed than anything in the database. The right takes effect only after the SQL Server service restarts, which the installer reports and leaves to you; then run the step again to check it is recording. A domain Group Policy can override the audit policy. |
| `-AppPoolSqlLogin` | Override the account that gets the login. | Worked out from the App Pool | Leave unset | Only needed on a server that isn't domain-joined, or similar. |
| `-SeedTestData` | Load test users and synthetic data. | Prompted (never asked for Production) | `$false` | Test data and test sign-ins don't belong in a live install. |

### Nightly import

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| `-InstallNightlyJob` | Install the nightly Import and Load SQL Server Agent job (runs at 02:00). | Prompted | `$true` once export files are in place | Without it, data is loaded only by hand. |
| `-ImportSources` | Which CyberArk sources the job imports: Both, PrivilegeCloud or SelfHosted. Sets the CyberArk sources on first install only. | Prompted | Only the sources you have | A source that is switched on but missing fails the job each night. |
| `-ExportFolderPath` | The Privilege Cloud export folder, as seen by the SQL Server service account. First install only. | Prompted if needed | A local folder on the SQL Server | SQL Server reads it directly. |

After the first install, these three are changed on **Admin > Data Sources** ("CyberArk sources", in section 3); re-running the installer doesn't overwrite what's saved there.

### IIS and certificate

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| `-SiteName` | The IIS site name. The App Pool is named `<SiteName>-AppPool`. | `BlueTrack` | `BlueTrack` | Also names the installer's saved answers. |
| `-ApiInstallPath` / `-WebInstallPath` | Where the API and web pages are installed. | `<system drive>\inetpub\BlueTrack\<Environment>\api` and `...\web` | The defaults | Kept separate per environment. |
| `-Hostname` | The site's host name. | Prompted | The DNS name users will type | It must match the certificate. |
| `-HttpPort` / `-HttpsPort` | The site bindings. | 80 / 443 | 443 for HTTPS | Users should always use HTTPS; the OIDC sign-in cookie is never sent over plain HTTP. |
| `-CertificateThumbprint` | An existing certificate in `LocalMachine\My` for the HTTPS binding. | Prompted; blank generates a self-signed certificate | A certificate from your organization's CA, with a Subject Alternative Name for the host name | Browsers trust it without warnings; Chrome and Edge reject a certificate without a SAN. With a CA-issued certificate the installer also turns on HSTS. |
| `-GenerateSelfSignedCert` | Create a self-signed certificate. | Off | Development and Test only | Browsers warn about it, and users learn to click through warnings. |
| `-ResetIis` | Run `iisreset` after the IIS steps. | Prompted | `$true` for a first install, if no other site is busy | Makes every IIS change take effect, but briefly stops every site on the server. |
| `-Force` | Recreate the App Pool, site and application even if they exist. | Off | Off | Only to repair a broken IIS setup. |

To replace a certificate later, run the installer with `-Step Iis.Site -CertificateThumbprint <new thumbprint>`.

### Allowed folders and other server settings

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| `-AllowedFileRoots` | The only folders data feeds may read and backups may be written to. Written to `FileAccess:AllowedRoots` in the settings file. | None: any local folder, no network shares | List only your feed folder and backup folder, for example `'D:\Feeds','D:\Backups\BlueTrack'` | The server reads and writes these paths as itself, so a short list stops an admin page reaching other files or shares. Add a share here only if you need one. |
| `-BackupFolder` | Where the installer's automatic pre-update backup goes, when the database already exists. | Prompted only if needed | The same backup folder | Keeps all backups together under the same access rules. |
| `-SkipPreDeployBackup` | Skip that automatic backup. | Off | Off | The backup is your way back if an update fails. |
| `-SkipSmokeTest` / `-SkipPrerequisiteInstall` | Skip the post-install check, or only report missing prerequisites. | Off | Off | The smoke test confirms the site works before users arrive. |
| `-PrerequisiteSource` / `-InstallerSourcePath` | Where prerequisite installers come from: Auto, Offline, Winget or Download, and an offline folder. | Auto | Offline, from a folder you've checked, for servers without internet | Every installer must also carry a valid vendor signature, whichever source is used. |

**Settings file.** The installer's `Db.AppSettings` step writes `appsettings.Site.json` into the API folder with four settings: the database connection string, `Security:EnableHsts` (on only when the certificate isn't self-signed), `FileAccess:AllowedRoots` and `BlueTrack:EnvironmentName` (the `-Environment` answer, shown on the Deployment page). BlueTrack always reads this file, whatever the environment. The web pages can't change any of it. To change allowed folders later, re-run the installer with the new `-AllowedFileRoots` (steps `Db.AppSettings,Iis.WebConfig`: the API reads the file when it starts, and `Iis.WebConfig` recycles the App Pool) rather than editing the file by hand, so the saved answers stay in step. When the connection string holds a SQL password, the installer limits the file to Administrators, SYSTEM and IIS worker processes.

Earlier installers wrote `appsettings.<Environment>.json` instead, which BlueTrack read only on a Production install. `Db.AppSettings` removes that old file. An old `appsettings.Production.json` that holds settings the installer didn't write is kept, with a warning naming them: move those settings into `appsettings.Site.json` by hand, then delete it. After this upgrade, a site with a trusted certificate starts sending HSTS.

**Site URL.** The installer's `Db.SiteUrl` step sets the Site URL on Global Application Configuration to `https://<Hostname>` (with `:<HttpsPort>` when that isn't 443), only when it's blank.

### App Pool identity

The installer creates the App Pool with the built-in `ApplicationPoolIdentity`. You can change it afterwards in IIS Manager (the App Pool's **Advanced Settings > Identity**).

| Setting | What it does | Default | Recommended | Why |
|---|---|---|---|---|
| App Pool identity | The Windows account BlueTrack runs as: it connects to SQL Server, reads feed folders, manages backup folder permissions and decrypts User-scope credentials. | `ApplicationPoolIdentity` | A dedicated gMSA (group managed service account) | Its access belongs to BlueTrack alone, it is the same account whether SQL Server is local or remote, and Windows manages its password. With a remote SQL Server, `ApplicationPoolIdentity` connects as the computer account, which every other service on the server shares. |
| Load User Profile | Lets the App Pool's account use its own certificate store. | IIS default | On, if the SAML SP certificate is in the account's own store | Otherwise put the certificate in `LocalMachine\My`. |

After changing the identity: give the new account its SQL login (`-Step Db.AppPoolAccess`), read access to feed folders, Modify on the backup folder, and access to the SAML certificate's private key. Re-enter User-scope DPAPI credentials. Then check Windows sign-in still works; if you register the site's `HTTP/<hostname>` SPN on the gMSA, IIS's `useAppPoolCredentials` must also be on. `Admin_Installation.md` has the step-by-step gMSA setup.

### Fixed behaviour (not configurable)

These values are built in. They are listed so you know what to expect.

| Behaviour | Value |
|---|---|
| Cached rights, longest lifetime | 15 minutes, whatever the idle timeout |
| OIDC session cookie | 8 hours; HTTPS only |
| Account Progress lock renewal while a page is open | Every minute |
| CyberArk CP and CCP fallback to last fetched value | Up to 24 hours |
| Nightly Import and Load job | 02:00 (when installed) |
| Audit Log Purge job | 03:00 (when installed by hand) |
| Notification checks | Every 5 minutes (DevFakeAuthEnabledTooLong repeats at most every 7 days) |
| OIDC changes | Need an application restart |
