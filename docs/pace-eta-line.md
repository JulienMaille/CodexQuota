# Design: simple pace-ETA line in the flyout

Status: implemented (simple version) — single flyout line, no notifications, no settings.
Area: `CodexUsagePanel` (flyout body) · new `PaceLine` helper

## Problem

The flyout tells the user where they stand (`Weekly · 100% · resets in 6d 23h`) but not
*where they are going*. Burning tokens faster than the week's allowance supports a "resets in
6d 23h" that is misleading: at current pace the cap is hit in 3 days, not 7. The user needs one
glanceable signal: **at the current pace, when does the weekly quota give out?**

## Goal

A single compact line under the weekly meter, visible only when the data supports it:

> Pace: ~5.2k tokens/day → cap by **Wed** · ~3.1 days left

and a one-word urgency tint when the pace threatens the reset:

- `ok` — projected depletion after the next reset → "resets before you exhaust it"
- `tight` — depletion lands between 3d and today → short warning color
- `burned` — projected depletion ends before the reset boundary; render "pace: too hot" style.

No charts, no second axis, no settings. If any input is missing, the line hides (never guesses).

## Data sources (already in code)

| Input | Type | Where |
|---|---|---|
| Weekly remaining | quota → remaining tokens | `UsageResult` via `QuotaDisplay.RemainingPercent` |
| Daily token burn | `ProfileUsageBucket[]` (`StartDate`, `Tokens`) | `CodexProfileSnapshot.DailyUsageBuckets` (Profile endpoint) |
| Reset boundary | reset timestamp | `UsageResult.Reset` (countdown row) |

Notes already documented on `CodexProfileSnapshot`: the Profile endpoint lags and **omits the
current day** — the pace math below compensates (see "Current-day gap").

## Algorithm (keep it simple)

1. **Daily burn rate** = mean of the last N closed profile days, N = `min(7, buckets.Count)`.
   Skipped days (StartDate increments missing) are counted as 0-burn days — a rest day lowers the
   mean instead of being dropped, which is the honest reading of "pace".
2. **Current-day gap**: the profile omits today's tokens. Add today's *usage delta* (from the
   quota fetch: `limit - remaining` moved since the last snapshot), clamped ≥ 0, into the burn
   window as a partial day.
3. **Burn-adjusted days left** `=` remainingTokens / dailyRate.
4. **Cap date** = `now + daysLeft` (calendar day, e.g. `Wed`).
   - if `cap ≤ reset`: `tight`/`burned` when `reset - cap ≤ 2d` / `cap == now`
   - else `ok`.
5. **Render** (single TextBlock, secondary brush):

```
pace ≈ {rate:0.#}k tok/day → cap ~{date} {+ countdown}{state chip}
```

`rate` formatting: `≥10k → "10k"`, `≥1k → "5.2k"`, `95 → "950"`, `<10 tokens/day → line hidden`.

### Edge cases

- insufficient history (`buckets < 2` days, or no quota/remaining): hide the line.
- rate ≈ 0 (quota flat): hide (nothing to say).
- negative daysLeft (already over? cap): show `cap NOW` in critical color.
- Profile lag: burn only counts closed days + the in-flight delta — no double counting.

## Where it renders

- `CodexUsagePanel` — new 1-row grid under the weekly meter row, `Visibility` toggled by the
  formatter's result (Hidden when inputs incomplete). Not in the widget tile (too narrow) — the
  tile already gets the percentile + color; the pace line is a flyout-only detail.
- Font: `CaptionTextBlockStyle`; color: built from `QuotaDisplay.BrushKeyForRemaining(daysLeft/maxDays)
  ` so the caution/critical brush flow is reused unchanged.

## Implementation sketch (as shipped)

```csharp
// src/CodexQuota.App/Services/PaceLine.cs
public sealed record PaceLineResult(string Label, double RemainingPercent);
public static class PaceLine
{
    // Inputs already available at publish time; pure function => fully unit-testable.
    // weeklyUsedPercent / weeklyResetAt / weeklyWindowMinutes come from the weekly RateWindow
    // (the API reports used-percent, not raw remaining tokens — see adaptation below).
    public static PaceLineResult? Compute(
        IReadOnlyList<ProfileUsageBucket> dailyBuckets,   // newest last
        double weeklyUsedPercent,
        DateTimeOffset? weeklyResetAt,
        int weeklyWindowMinutes,
        DateTimeOffset now)
}
```

- Pure by construction — same inputs, same line; no timers, no service state.
- Recompute on every publish (the coordinator already re-publishes after each fetch) — no new
  cadence, no coordination changes.

### Adaptation from this design (locked in code + tests)

- The usage API exposes only `used_percent` + `limit_window_seconds` + `reset_at` for the weekly
  window — no token cap, so "remainingTokens" does not exist in `RateWindow`. The projection is
  computed on the used-percent slope instead: `daysToCap = (100 - used) * elapsed / used`, with
  `elapsed = now - (resetAt - windowMinutes)`. The bucket mean supplies the human "~5.2k tok/day"
  label only.
- `RemainingPercent` (output) = `daysToCap / daysToReset` clamped to 0..100, fed into the existing
  `QuotaDisplay.BrushKeyForRemaining` so the configured 50/20 thresholds color the line (this is
  the doc's open question, resolved toward the fold-in).
- Bucket dates are not parsed; only their count and token values matter (≤ 7 newest buckets).
  Rest days are literal 0-token buckets in the feed, so they lower the mean naturally.

## Tests

`PaceLineTests` (xUnit, mirrors `QuotaDisplayTests` style):
table-driven cases — steady burn → expected cap date; zero/one-day history → null (hidden);
over-quota today → "cap reached" + 0%; profile lag gap counted correctly; only the last 7 buckets
feed the rate; rest days lower the mean; 5.2k / 10k / 950 rate formatting.

## Open question (answered in code)

Color grammar: **RemainingPercent** = daysToCap / daysToReset maps through
`QuotaDisplay.BrushKeyForRemaining` — the pace line folds into the configured `Warn below %`
thresholds (default 50/20), consistent with the existing "urgency color" language in the
tile/flyout. A pace that exhausts the cap before the reset reads ≤ 100% and colors amber/red by
the user's thresholds; a pace that resets first clamps at 100% (default text color).