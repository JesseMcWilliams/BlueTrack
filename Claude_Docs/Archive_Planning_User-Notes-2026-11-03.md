# BlueTrack: The user's notes of 2026-11-03

> **Stage: Archive (finished 2026-11-04).** Every note below was built or answered in D-302 and D-305 to D-329 (PRs #246 to #271). The notes were checked against the code on 2026-11-03 and kept in `Planning_Backlog.md` until the last one shipped. The design behind each answer is in the Decision Register and the `Design_` doc it names.

## Backlog items

| # | Note (the user's words) | Outcome | Decisions | PR |
|---|---|---|---|---|
| 1 | How do you add the CP after the fact? | Re-run the installer's `Build.Api` step once the SDK is present; the error says so when CP is chosen but not built in. Documented in the Installation and Upgrade guides. | D-310 | #252 |
| 2 | Add a version file for the release to make it easy to identify. | `VERSION` at the repo root, and `BlueTrack.version.txt` beside the published API. | D-311 | #253 |
| 3 | Remove or investigate the Credential property on Secrets store configuration. | The Credential box shows only for the backends that read it (Conjur, Azure service principal, AWS access key). | D-306 | #247 |
| 4 | Add reason to CP and CCP requests. | An optional Reason on the CP and CCP settings, sent with each retrieval. | D-309 | #251 |
| 5 | Make sure PVWA calls handle LB. | Sticky sessions documented (one logon per pull); later, Other PVWA addresses with failover. | D-312, D-320 | #254, #262 |
| 6 | Data source test looks to have run the export. It should be a quick test. | Test is quick; Test with a full read is its own button. | D-313 | #255 |
| 7 | BlueTrack Data Source PVWA run Details is all white. | An undefined color variable; a test now fails on any undefined `--color-*`. | D-306 | #247 |
| 8 | Add page to show PowerBI access information. | Admin > Power BI; the installer's `Db.PowerBiAccess` step creates the reader role. | D-314, D-321 | #256, #263 |
| 9 | Does the run now on data source run the update tasks? | Yes: Pull now runs the full Import and Load (not the daily snapshot). Already built. | D-302 | n/a |
| 10 | Need to add the safe information to the account details and on the account page. | The Safe on Account Detail and a Safe column on the Accounts list. | D-308 | #250 |
| 11 | Make the safe a link to the safe details. | A Safe details page, linked from both. | D-308 | #250 |
| 12 | Safe details should show if it is assigned to an application. | The BlueTrack application mapped to the Safe. | D-308 | #250 |
| 13 | The account details should show as well. | The Safe's accounts, each linked to Account Detail. | D-308 | #250 |
| 14 | Risk Score is missing Bulk options | Bulk set or clear of the override on Reports > Risk Score. | D-315 | #257 |
| 15 | Allow import of updates as well. | A Target re-import updates the risk score, then name, type and description; the Accounts import takes risk score overrides. | D-316, D-322, D-323 | #258, #264, #265 |
| 16 | Recalculate now doesn't seem to be working. | Fixed. | D-306 | #247 |
| 17 | Double check the Target and Group mapping. Where is it and how do we get that data? | Already built: the Targets and Access Groups pages, their imports and data feeds. | D-101 to D-104, D-137, D-193 | n/a |
| 18 | LDAP/AD query should be under data sources. | AD Account Discovery on Admin > Data Sources (run time, Run now, run log); domains ticked Use as a data source; domains for discovery only. | D-317, D-324, D-328 | #259, #266, #270 |
| 19 | On accounts Progress. Allow export of data and reimport with updated values. | Accounts Export and Import changes (CSV or Excel, an opt-in to clear blanks, overrides). | D-318, D-323 | #260, #265 |
| 20 | On typed search boxes, don't search, require Apply Filter or wait. | Search 400 ms after typing stops. | D-306 | #247 |
| 21 | Where is shadow pull set? | Removed with the EVD source; nothing to set. | D-278 | n/a |
| 22 | Should include built in groups in safe query | Safe members were already read with `includePredefinedUsers eq true`; built-in Safes recognised as internal; an option to read them. | D-270, D-319, D-327 | #261, #269 |
| 23 | AccountsFeedDiscoveryLogs is example | As 22. The data the option reads still needs reviewing for usefulness. | D-319, D-327 | #261, #269 |
| 24 | Check on DNA ingestion | DNA account-list workbooks uploaded on Admin > Data Sources, listed on Reports > DNA Accounts. | D-325 | #267 |

## Key KPIs

| # | Note | Outcome | Decisions | PR |
|---|---|---|---|---|
| 1 | All Discovered Accounts. | The funnel starts with Everything discovered: tracked accounts plus untracked AD and DNA accounts, dismissed included, duplicates and linked accounts once. | D-326, D-329 | #268, #271 |
| 2 | Accounts in scope versus out of scope. | Already built (the KPI Summary). | D-143 | n/a |
| 3 | Of accounts in scope, what has been onboarded. | Already built. | D-143 | n/a |
| 4 | Of accounts onboarded, what is being managed. | Already built. | D-143 | n/a |
| 5 | Of accounts being managed, what is compliant. | Already built. | D-143 | n/a |

The landing page asked for while answering these notes became Home (D-305, then D-307; #246, #248).
