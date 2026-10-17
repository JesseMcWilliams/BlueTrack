# BlueTrack: Questions backlog

> **Stage: Planning.** Questions Claude couldn't ask the user at the time because the user asked it to keep working without stopping for input. Each says what was assumed meanwhile. The user answers by number; Claude then records the answer as a `D-n` (or in the doc it affects), changes the code if the answer differs from the assumption, and deletes the question here.

## Open
1. **Can a key's role or expiry be changed after it is created?** (D-243) Assumed no: delete it and create a new one, or rotate it (phase 3b). Changing a role in place would need the rights cache cleared for that key.
2. **Who should get the key-expiry emails?** (D-243, phase 3b) Options: the notification recipients of one role (as the D-222 emails do), or an **owner email** entered on each key. Built (D-243, phase 3b): the notification type's recipients, as for every other notification (Admin > Notifications). A per-key owner email is not built; add one if the script owners aren't the admins.
3. **Should expired keys be deleted automatically after some time?** (D-243) Assumed no: they stay in the list as Expired until an admin deletes them.
4. **Is 600 requests a minute per key the right rate limit, and should it be per key?** (D-243) Built: 600 a minute, the same for every key, a setting on Global Application Configuration. A per-key limit is not built.
5. **Should a Group mapping accept any valid token from the provider that carries the group?** (D-243, phase 2) Built that way: a token issued to a person for another application, with the group, is accepted. The alternative is to accept only tokens whose client ID has a ClientId mapping, and use groups only to pick among those. Stricter, but then a group alone can't give a script its role.
6. **Are SAML identity providers wanted for tokens?** (D-243, phase 2) Not built: only OIDC providers are accepted (a SAML assertion isn't a bearer token a script can fetch on its own).
7. **When an identity provider's token matches several mappings, is "lowest order wins" right?** (D-243, phase 2) Built that way (a client match before a group match at equal order), with the order shown in the mapping list.
