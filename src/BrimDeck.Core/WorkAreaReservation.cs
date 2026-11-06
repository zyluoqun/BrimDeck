namespace BrimDeck.Core;

public static class WorkAreaReservation
{
    // Physical pixels below the existing work-area edge. Use the configured capsule rather than a window rule's line,
    // so maximizing a window cannot change the reservation and then change that window's context again.
    public static int Height(DeckSettings settings, DipRect monitor, DipRect work, double pixelsPerDip)
    {
        if (!settings.CapsuleAvoidanceEnabled
            || !(pixelsPerDip > 0) || !double.IsFinite(pixelsPerDip)) return 0;
        var capsule = CollapsedGeometry.Resolve(settings, CompactStyle.Capsule, monitor, work, pixelsPerDip,
            panelScale: PanelScales.Factor(settings));
        return (int)Math.Clamp(Math.Ceiling((capsule.Surface.Bottom - work.Y) * pixelsPerDip), 0,
            Math.Max(0, Math.Floor(work.Height * pixelsPerDip) - 1));
    }
}
