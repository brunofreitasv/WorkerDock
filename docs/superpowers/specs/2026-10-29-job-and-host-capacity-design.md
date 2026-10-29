# Job and host capacity with review behavior

## Intent

Operators need to know why a queued run is waiting, change how many runs of a job may execute together, and keep unrelated work moving when an execution needs review. The current template limit of 1 silently blocks jobs that share a template despite a host capacity of 10. Job capacity is already stored and has a management API, but no UI. `NeedsReview` currently holds a job admission slot.

## Goals

- Two capacity limits: host capacity across all jobs and job capacity for each job. Only actively running attempts count against either numeric limit. Queued, `RetryWaiting`, and `NeedsReview` consume no numeric capacity.
- No template capacity limit or template-specific capacity control. A template may use any available host capacity, subject to the jobs' own limits.
- Managers can see and change job capacity in the job UI. The default is 1. `Allow` permits parallel runs up to job and host capacity; `Queue` remains serial; `Skip` drops overlapping occurrences.
- One review setting appears with Overlap. For `Queue`, label it “Wait for review before starting new runs”; for `Skip`, label it “Skip new runs while review is pending.” Both labels control the same per-job setting. `Allow` continues regardless and needs no review switch.
- Existing jobs start with the setting on; newly created jobs start with it off. Operators can change it later. Historical execution and attempt records remain intact.
- The UI explains which rule holds or skips a run, including capacity, review hold, and overlap, without implying that `NeedsReview` consumes capacity.

## Behavior

| Overlap | Earlier `NeedsReview`, setting on | Earlier `NeedsReview`, setting off |
| --- | --- | --- |
| Queue | New runs remain queued until review is resolved. | New runs may start in order when no run of that job is running. |
| Skip | New occurrences are skipped with a review-overlap reason. | New occurrences may start when no run of that job is running. |
| Allow | New runs may start within job and host capacity. | Same behavior. |

`RetryWaiting` does not consume numeric capacity or trigger the review hold. When due, a retry competes for capacity under the job's overlap rules. A running attempt continues to count even if a stop has been requested, until it actually stops. If a limit is reduced below the number already running, existing attempts continue; no further starts occur until the count falls below the new limit.

Preserve `AdmissionSequence` order within each `Queue` job. A run waiting on that job's running attempt or review hold must not delay eligible runs of other jobs. Across eligible jobs, preserve the current priority and due-time ordering; host capacity is assigned to ready candidates in that order. A job whose limit is reached does not reserve host capacity.

## Approaches

1. **Chosen: job setting plus the existing Overlap rule.** One switch expresses whether `NeedsReview` should affect Queue or Skip; their existing meanings determine wait or skip. This is the smallest UI choice and keeps the current default for existing jobs.
2. A separate Continue/Wait/Skip selector for review cases offers more combinations but duplicates Overlap and makes job setup harder to understand.

For template capacity, remove its runtime gate rather than mirror host capacity into every template row. Mirrored values can become stale and still look like independent limits. The old database column may remain inert for compatibility until a cleanup migration, but the setter endpoint and documentation must not present it as an active limit.

## Implementation boundaries

- Store the review setting as a live per-job value alongside `ConcurrencyLimit`, not in immutable revision JSON. A tenant migration sets existing jobs to enabled and gives new rows a disabled default. Update `current-state.sql` with the same schema and migration history.
- Capacity and review settings require Manage permission. Extend job management reads and a management command to expose and update them; retain the existing job-limit API for compatibility. Never silently change an active job's limit while editing unrelated definition fields.
- The create wizard shows Job Capacity (default 1) and the review switch (off) in its policy step. Job detail shows both values and lets managers change them. Capacity 0, if retained by the API, is explained as pausing starts; the UI should offer an unambiguous way back to 1. Read-only users see the values without controls.
- Saving a new job must apply its chosen live settings before enabling it. If that update fails, leave the job disabled, keep its draft, and show the error. Editing a job's policy and live settings must make clear which change is being saved.
- Remove template limit checks from queue selection and transactional claim recheck, and prevent the old template-limit command from claiming to set a usable control. All start paths must enforce host and job running counts consistently under concurrency.
- Review resolution remains a separate explicit action and does not erase an abandoned attempt. Switching off the review hold may allow later runs while the earlier execution still needs review.

## Non-goals and constraints

No automatic retry or automatic resolution of an abandoned attempt. No new template-level capacity setting. Existing job history and pinned execution details remain accessible. The user requested this design before implementation; implementation follows a separate plan after approval.
