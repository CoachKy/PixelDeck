namespace PixelDeck.App.Input;

/// <summary>
/// Maps configured controller slots onto the devices actually connected.
/// </summary>
/// <remarks>
/// A configured index is a preference, not a guarantee. Nothing stops a profile
/// naming a slot with no controller in it — the usual way in is a saved
/// assignment for a second pad that is no longer plugged in — and pinning a
/// player to an empty slot leaves them with no input and nothing on screen
/// explaining why. The symptom is that a game simply ignores the controller
/// until someone works out the fix is on the settings page.
///
/// Players are resolved in order, and that ordering is the important part: with
/// one controller plugged in, player one gets it even if a later player's saved
/// preference happens to name that slot. Honouring preferences ahead of player
/// order reintroduces the original fault, because player two claims the only pad
/// and player one is left with nothing.
///
/// Saved preferences are deliberately not rewritten. Plug the second pad back in
/// and the original assignment applies again.
/// </remarks>
internal static class ControllerAssignment
{
    /// <summary>
    /// Rewrites <paramref name="assignments"/> in place so every player that can
    /// have a connected device has one, and no two players share a device.
    /// </summary>
    public static void Resolve(Span<int> assignments, GamepadConnections connections)
    {
        if (connections.Count == 0)
        {
            // Nothing to resolve against. Leaving the preferences untouched keeps
            // the settings page showing what the player chose.
            return;
        }

        Span<bool> taken = stackalloc bool[GamepadManager.MaximumControllers];

        for (var player = 0; player < assignments.Length; player++)
        {
            var wanted = assignments[player];

            // A preference naming a pad that is present and unclaimed is kept, so
            // a deliberate multi-controller setup survives untouched.
            if (connections.IsConnected(wanted) && !taken[wanted])
            {
                taken[wanted] = true;
                continue;
            }

            var slot = FirstFree(connections, taken, requireConnected: true);
            if (slot < 0)
            {
                // No device left for this player. Park them on an unclaimed empty
                // slot rather than leaving them pointing at someone else's pad,
                // which would have two players driving one controller.
                slot = FirstFree(connections, taken, requireConnected: false);
            }

            if (slot >= 0)
            {
                taken[slot] = true;
                assignments[player] = slot;
            }
        }
    }

    private static int FirstFree(GamepadConnections connections, Span<bool> taken, bool requireConnected)
    {
        for (var slot = 0; slot < GamepadManager.MaximumControllers; slot++)
        {
            if (taken[slot] || (requireConnected && !connections.IsConnected(slot)))
            {
                continue;
            }

            return slot;
        }

        return -1;
    }
}
