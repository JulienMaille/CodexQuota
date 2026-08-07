using System;
using System.Collections.Generic;
using CodexQuota.Taskbar;
using CodexQuota.Usage;

namespace CodexQuota.Tests;

/// <summary>
/// A single Codex tile is always shown, so there is no multi-provider ladder to solve (the old layout
/// solver and its pin-budget companion are gone with the other providers). What survives as testable
/// logic in <see cref="TaskBarWidget"/> is the recency rank used to order tiles: least recently used
/// first, so whatever was last worked in stays when a row overflows the measured gap.
/// </summary>
public class TaskBarWidgetTileFitTests
{
    // A provider that has never been focused is the first thing to give up its tile, so it has to sort
    // behind every provider that has been.
    [Fact]
    public void NeverActiveSortsLeastRecent()
    {
        var recent = new List<ProviderId> { ProviderId.Codex };

        // ProviderId has only Codex now, so the "never active" case collapses to an empty recency list.
        Assert.Equal(int.MaxValue, TaskBarWidget.RecencyOf(ProviderId.Codex, Array.Empty<ProviderId>()));
        Assert.True(TaskBarWidget.RecencyOf(ProviderId.Codex, Array.Empty<ProviderId>())
            > TaskBarWidget.RecencyOf(ProviderId.Codex, recent));
    }

    [Fact]
public void RecentlyActiveProviderSortsFirst()
    {
        var recent = new List<ProviderId> { ProviderId.Codex };
        Assert.Equal(0, TaskBarWidget.RecencyOf(ProviderId.Codex, recent));
    }

    [Fact]
    public void EmptySlotSortsLeastRecent()
        => Assert.Equal(int.MaxValue, TaskBarWidget.RecencyOf(null, Array.Empty<ProviderId>()));

    [Fact]
    public void FirstOccurrenceWinsWhenAProviderRepeats()
    {
        var recent = new List<ProviderId> { ProviderId.Codex, ProviderId.Codex };

        Assert.Equal(0, TaskBarWidget.RecencyOf(ProviderId.Codex, recent));
    }
}