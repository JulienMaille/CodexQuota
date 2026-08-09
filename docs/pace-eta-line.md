# Design: simple pace-ETA line in the flyout

Status: implemented - single flyout line, no notifications, configurable workday assumption.
Area: `CodexUsagePanel` (flyout body) · `PaceLine` helper

## Problem

The flyout tells the user where they stand (`Weekly · 100% · resets in 6d 23h`) but not
*where they are going*. Burning faster than the week's allowance makes a reset countdown
misleading: at the current pace the cap may be hit before the window resets. The user needs one
glanceable signal: **at the current pace, when does the weekly quota give out?**

## Goal

A single compact line under the weekly meter, visible when the weekly window has enough data:

> Pace ~1.4% quota/day → cap Wed (~3.1d)

The rate is expressed as percentage points of the weekly quota per day. This is independent of the
model mix and does not pretend that the service exposes a raw token cap.

No charts or second axis. If the weekly window is unavailable, the line hides rather than guessing.

## Data sources

| Input | Type | Where |
|---|---|---|
| Weekly used percentage | `used_percent` / `usage_percent` | `/wham/usage` weekly `RateWindow` |
| Window duration | `limit_window_seconds` | `/wham/usage` weekly `RateWindow` |
| Reset boundary | `reset_at` | `/wham/usage` weekly `RateWindow` |

The Profile endpoint and local Codex session journals still power the activity heatmap and live
current-day token counter. They are intentionally not inputs to the pace projection because they do
not identify or weight Luna/Sol and other model families consistently with the server quota.

## Algorithm

1. **Elapsed window** = `now - (resetAt - windowDuration)`.
2. **Elapsed workdays** = full 24-hour quota days plus the current partial day divided by the
   configured workday hours, capped at one workday.
3. **Quota rate** = `usedPercent / elapsedWorkdays`, expressed as percentage points per assumed
   workday. The default workday is 8 hours.
4. **Days to cap** = `(100 - usedPercent) / quotaRate`.
5. Compare actual usage with ideal elapsed-workday usage. A deviation of up to two percentage points is
   treated as on track, so a small first sample after reset does not produce a false runout warning.
6. **Cap date** = `now + daysToCap` only when a materially-ahead pace exhausts before reset; otherwise
   render `resets before cap`.
7. Feed `daysToCap / daysToReset` through the existing `QuotaDisplay.BrushKeyForRemaining` mapping
   for the warning tint. Non-warning pace is clamped to 100% (neutral).

### Formatting and edge cases

- `≥10` percentage points/day → whole number (`12% quota/day`).
- `≥1` percentage point/day → one decimal (`1.4% quota/day`).
- `<1` percentage point/day → up to two decimals (`0.14% quota/day`).
- No reset, a reset in the past, a window that has not started, or zero usage → hide the line.
- A fully used window → `cap reached` with critical color.

## Where it renders

- `CodexUsagePanel` — one caption line under the weekly meter.
- Not in the taskbar tile, which is too narrow and already has the quota percentage/color.
- The line is recomputed whenever a usage result is rendered; it does not wait for profile data.

## Implementation

```csharp
public static PaceLineResult? Compute(
    double weeklyUsedPercent,
    DateTimeOffset? weeklyResetAt,
    int weeklyWindowMinutes,
    DateTimeOffset now,
    int workdayHours = 8)
```

The function is pure and unit-testable. The weekly `RateWindow` is the only usage input; workday hours
is the user-configured assumption.

## Tests

`PaceLineTests` covers steady burn, slow burn, a fully used window, missing/past reset boundaries,
zero usage, the small-first-sample guard, percentage-points-per-day formatting, and the fact that
profile token history is not required.
