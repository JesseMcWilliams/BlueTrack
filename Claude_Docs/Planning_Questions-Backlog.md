# BlueTrack: Questions backlog

> **Stage: Planning.** Questions Claude couldn't ask the user at the time because the user asked it to keep working without stopping for input. Each says what was assumed meanwhile. The user answers by number; Claude then records the answer as a `D-n` (or in the doc it affects), changes the code if the answer differs from the assumption, and deletes the question here.

## Open
1. **Should expired API keys be deleted automatically after some time?** (D-243) Assumed no: they stay in the list as Expired until an admin deletes them.
2. **When an identity provider's token matches several mappings, is "lowest order wins" right?** (D-243, phase 2) Built that way (a client match before a group match at equal order), with the order shown in the mapping list.
3. **The live test of an identity provider token against the lab authentik was blocked** (2026-10-17): the permission system refused a script that tried several ways of getting a token (client credentials, then an authorization-code sign-in as the lab admin). authentik refused every client-credentials style with `invalid_grant`. Say how to proceed: allow that script (or add a permission rule for it), or give me a ready token and its client ID to test with, or accept the fake-provider tests as enough for now.
