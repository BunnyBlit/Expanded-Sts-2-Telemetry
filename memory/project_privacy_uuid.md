---
name: project-privacy-uuid
description: Privacy TODO — anonymize player and run IDs before any public data sharing using v5 UUIDs
metadata:
  type: project
---

Player Steam IDs and run start timestamps could be used to deanonymize users (start time + Steam ID = linkable to public Steam activity). Before any public data sharing or analytics pipeline exposure, both should be anonymized.

**Plan:** Replace raw IDs with deterministic v5 UUIDs, each with their own fixed namespace UUID constant:
- `player_id`: `UUID5(PLAYER_NAMESPACE, steam_id_string)`
- `run_id`: `UUID5(RUN_NAMESPACE, start_time_string)` (or combined with player for extra isolation)

Timestamps in events lack timezone info and are Unix seconds UTC — probably low enough resolution to be acceptable without transformation, but worth revisiting.

**Why:** Reversible if needed (namespace UUIDs are constants we control), no data loss for analytics, but breaks naive Steam profile lookups.

**How to apply:** Implement before opening any data pipeline to external parties. Current telemetry writes raw Steam64 IDs and Unix timestamps — fine for local/dev use.
