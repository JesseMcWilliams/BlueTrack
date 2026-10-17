# BlueTrack: Questions backlog

> **Stage: Planning.** Questions Claude couldn't ask the user at the time because the user asked it to keep working without stopping for input. Each says what was assumed meanwhile. The user answers by number; Claude then records the answer as a `D-n` (or in the doc it affects), changes the code if the answer differs from the assumption, and deletes the question here.

## Open
1. **The live test of an identity provider token against the lab authentik is still blocked** (2026-10-17): the permission system refused a script that tried several ways of getting a token (client credentials, then an authorization-code sign-in as the lab admin). authentik refused every client-credentials style with `invalid_grant`. The user asked what permissions are needed; the answer is in the conversation of that date. Next step: the user decides whether to allow one narrowly written script, to give a ready token and its client ID, or to accept the fake-provider tests for now.
